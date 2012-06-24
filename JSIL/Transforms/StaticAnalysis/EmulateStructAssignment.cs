﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.ILAst;
using JSIL.Ast;
using Mono.Cecil;

namespace JSIL.Transforms {
    public class EmulateStructAssignment : JSAstVisitor {
        public const bool Tracing = false;

        public readonly CLRSpecialIdentifiers CLR;
        public readonly IFunctionSource FunctionSource;
        public readonly TypeSystem TypeSystem;
        public readonly bool OptimizeCopies;

        private FunctionAnalysis2ndPass SecondPass = null;

        protected readonly Dictionary<string, int> ReferenceCounts = new Dictionary<string, int>();

        public EmulateStructAssignment (TypeSystem typeSystem, IFunctionSource functionSource, CLRSpecialIdentifiers clr, bool optimizeCopies) {
            TypeSystem = typeSystem;
            FunctionSource = functionSource;
            CLR = clr;
            OptimizeCopies = optimizeCopies;
        }

        protected bool IsImmutable (JSExpression target) {
            while (target is JSReferenceExpression)
                target = ((JSReferenceExpression)target).Referent;

            var fieldAccess = target as JSFieldAccess;
            if (fieldAccess != null) {
                return fieldAccess.Field.Field.Metadata.HasAttribute("JSIL.Meta.JSImmutable");
            }

            var dot = target as JSDotExpressionBase;
            if (dot != null) {
                if (IsImmutable(dot.Target))
                    return true;
                else if (IsImmutable(dot.Member))
                    return true;
            }

            var indexer = target as JSIndexerExpression;
            if (indexer != null) {
                if (IsImmutable(indexer.Target))
                    return true;
            }

            return false;
        }

        protected bool IsCopyNeededForAssignmentTarget (JSExpression target) {
            if (!OptimizeCopies)
                return true;

            if (IsImmutable(target))
                return false;

            var variable = target as JSVariable;
            if (variable != null)
                return SecondPass.ModifiedVariables.Contains(variable.Name);

            return true;
        }

        protected bool IsCopyNeeded (JSExpression value) {
            if ((value == null) || (value.IsNull))
                return false;

            while (value is JSReferenceExpression)
                value = ((JSReferenceExpression)value).Referent;

            var valueType = value.GetActualType(TypeSystem);
            var cte = value as JSChangeTypeExpression;
            var cast = value as JSCastExpression;

            TypeReference originalType;
            int temp;

            if (cte != null) {
                originalType = cte.Expression.GetActualType(TypeSystem);
            } else if (cast != null) {
                originalType = cast.Expression.GetActualType(TypeSystem);
            } else {
                originalType = null;
            }

            if (originalType != null) {
                originalType = TypeUtil.FullyDereferenceType(originalType, out temp);

                if (!TypeUtil.IsStruct(valueType) && !TypeUtil.IsStruct(originalType))
                    return false;
            } else {
                if (!TypeUtil.IsStruct(valueType))
                    return false;
            }

            if (valueType.FullName.StartsWith("System.Nullable"))
                return false;

            if (
                (value is JSLiteral) ||
                (value is JSNewExpression) ||
                (value is JSPassByReferenceExpression)
            ) {
                return false;
            }

            if (!OptimizeCopies)
                return true;

            if (IsImmutable(value))
                return false;
            
            // If the expression is a parameter that is only used once and isn't aliased,
            //  we don't need to copy it.
            var rightVar = value as JSVariable;
            if (rightVar != null) {
                int referenceCount;
                if (
                    ReferenceCounts.TryGetValue(rightVar.Identifier, out referenceCount) &&
                    (referenceCount == 1) && !rightVar.IsReference && rightVar.IsParameter &&
                    !SecondPass.VariableAliases.ContainsKey(rightVar.Identifier)
                ) {
                    if (Tracing)
                        Debug.WriteLine(String.Format("Returning false from IsCopyNeeded for parameter {0} because reference count is 1 and it has no aliases", value));

                    return false;
                }
            }

            var rightInvocation = value as JSInvocationExpression;
            if (rightInvocation == null)
                return true;

            var invokeMethod = rightInvocation.JSMethod;
            if (invokeMethod == null)
                return true;

            var secondPass = FunctionSource.GetSecondPass(invokeMethod);
            if (secondPass == null)
                return true;

            // If this expression is the return value of a function invocation, we can eliminate struct
            //  copies if the return value is a 'new' expression.
            if (secondPass.ResultIsNew)
                return false;

            // We can also eliminate a return value copy if the return value is one of the function's 
            //  arguments, and we are sure that argument does not need a copy either.
            if (secondPass.ResultVariable != null) {
                var parameters = invokeMethod.Method.Parameters;
                int parameterIndex = -1;

                for (var i = 0; i < parameters.Length; i++) {
                    if (parameters[i].Name != secondPass.ResultVariable)
                        continue;

                    parameterIndex = i;
                    break;
                }

                if (parameterIndex < 0)
                    return true;

                return IsCopyNeeded(rightInvocation.Arguments[parameterIndex]);
            }
 
            return true;
        }

        public void VisitNode (JSFunctionExpression fn) {
            // Create a new visitor for nested function expressions
            if (Stack.OfType<JSFunctionExpression>().Skip(1).FirstOrDefault() != null) {
                var nested = new EmulateStructAssignment(TypeSystem, FunctionSource, CLR, OptimizeCopies);
                nested.Visit(fn);
                return;
            }

            var countRefs = new CountVariableReferences(ReferenceCounts);
            countRefs.Visit(fn.Body);

            SecondPass = FunctionSource.GetSecondPass(fn.Method);

            VisitChildren(fn);
        }

        public void VisitNode (JSPairExpression pair) {
            if (IsCopyNeeded(pair.Value)) {
                if (Tracing)
                    Debug.WriteLine(String.Format("struct copy introduced for object value {0}", pair.Value));

                pair.Value = new JSStructCopyExpression(pair.Value);
            }

            VisitChildren(pair);
        }

        protected bool IsParameterCopyNeeded (FunctionAnalysis2ndPass sa, string parameterName, JSExpression expression) {
            if (!IsCopyNeeded(expression))
                return false;

            if (!OptimizeCopies)
                return true;

            bool modified = true, escapes = true, isResult = false;

            if (parameterName != null) {
                modified = sa.ModifiedVariables.Contains(parameterName);
                escapes = sa.EscapingVariables.Contains(parameterName);
                isResult = sa.ResultVariable == parameterName;
            }

            return modified || (escapes && !isResult);
        }

        public void VisitNode (JSInvocationExpression invocation) {
            FunctionAnalysis2ndPass sa = null;

            if (invocation.JSMethod != null)
                sa = FunctionSource.GetSecondPass(invocation.JSMethod);

            var parms = invocation.Parameters.ToArray();

            for (int i = 0, c = parms.Length; i < c; i++) {
                var pd = parms[i].Key;
                var argument = parms[i].Value;

                string parameterName = null;
                if (pd != null)
                    parameterName = pd.Name;

                if (IsParameterCopyNeeded(sa, parameterName, argument)) {
                    if (Tracing)
                        Debug.WriteLine(String.Format("struct copy introduced for argument {0}", argument));

                    invocation.Arguments[i] = new JSStructCopyExpression(argument);
                } else {
                    if (Tracing)
                        Debug.WriteLine(String.Format("struct copy elided for argument {0}", argument));
                }
            }

            VisitChildren(invocation);
        }

        public void VisitNode (JSDelegateInvocationExpression invocation) {
            for (int i = 0, c = invocation.Arguments.Count; i < c; i++) {
                var argument = invocation.Arguments[i];

                if (IsCopyNeeded(argument)) {
                    if (Tracing)
                        Debug.WriteLine(String.Format("struct copy introduced for argument {0}", argument));

                    invocation.Arguments[i] = new JSStructCopyExpression(argument);
                }
            }

            VisitChildren(invocation);
        }

        public void VisitNode (JSBinaryOperatorExpression boe) {
            if (boe.Operator != JSOperator.Assignment) {
                base.VisitNode(boe);
                return;
            }

            if (IsCopyNeeded(boe.Right)) {
                var rightVars = boe.Right.AllChildrenRecursive.OfType<JSVariable>().ToArray();
                // Even if the assignment target is never modified, if the assignment *source*
                //  gets modified, we need to make a copy here, because the target is probably
                //  being used as a back-up copy.
                var rightVarsModified = (rightVars.Any((rv) => SecondPass.ModifiedVariables.Contains(rv.Name)));

                if (rightVarsModified || IsCopyNeededForAssignmentTarget(boe.Left)) {
                    if (Tracing)
                        Debug.WriteLine(String.Format("struct copy introduced for assignment rhs {0}", boe.Right));

                    boe.Right = new JSStructCopyExpression(boe.Right);
                } else {
                    if (Tracing)
                        Debug.WriteLine(String.Format("struct copy elided for assignment rhs {0}", boe.Right));
                }
            }

            VisitChildren(boe);
        }
    }

    public class CountVariableReferences : JSAstVisitor {
        public readonly Dictionary<string, int> ReferenceCounts;

        public CountVariableReferences (Dictionary<string, int> referenceCounts) {
            ReferenceCounts = referenceCounts;
        }

        public void VisitNode (JSVariable variable) {
            int count;
            if (ReferenceCounts.TryGetValue(variable.Identifier, out count))
                ReferenceCounts[variable.Identifier] = count + 1;
            else
                ReferenceCounts[variable.Identifier] = 1;

            VisitChildren(variable);
        }
    }
}
