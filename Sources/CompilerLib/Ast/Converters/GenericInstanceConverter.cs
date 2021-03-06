﻿using System;
using System.Collections.Generic;
using System.Linq;
using Dot42.CompilerLib.Ast.Extensions;
using Dot42.CompilerLib.XModel;
using Dot42.FrameworkDefinitions;
using Dot42.Utility;

namespace Dot42.CompilerLib.Ast.Converters
{
    /// <summary>
    /// Add/expand generic instance type information
    /// </summary>
    internal static class GenericInstanceConverter 
    {
        enum TypeConversion
        {
            // will not change the type
            None,
            // Will change to nullable marker class if Nullable<T>, will load/create a generic
            // proxy if generic instance like IEnumerable<T>, will keep the primitive type.
            EnsureTrueOrMarkerType,
            // will make sure that the type is neither a marker class nor a primitive type
            EnsureRuntimeType,
        }

        /// <summary>
        /// Optimize expressions
        /// </summary>
        public static void Convert(AstNode ast, MethodSource currentMethod, AssemblyCompiler compiler)
        {
            var typeSystem = compiler.Module.TypeSystem;

            // Expand typeof
            foreach (var node in ast.GetSelfAndChildrenRecursive<AstExpression>(x => x.Code == AstCode.TypeOf))
            {
                var type = (XTypeReference) node.Operand;
                var typeHelperType = compiler.GetDot42InternalType(InternalConstants.TypeHelperName).Resolve();
                var loadExpr = LoadTypeForGenericInstance(node.SourceLocation, currentMethod, type, compiler, typeHelperType, typeSystem, TypeConversion.EnsureTrueOrMarkerType);
                node.CopyFrom(loadExpr);
            }

            // Expand instanceOf
            foreach (var node in ast.GetSelfAndChildrenRecursive<AstExpression>(x => x.Code == AstCode.SimpleInstanceOf))
            {
                var type = (XTypeReference)node.Operand;
                var gp = type as XGenericParameter;
                if (gp == null) continue;

                var typeHelperType = compiler.GetDot42InternalType(InternalConstants.TypeHelperName).Resolve();
                var loadExpr = LoadTypeForGenericInstance(node.SourceLocation, currentMethod, type, compiler, typeHelperType, typeSystem, TypeConversion.EnsureRuntimeType);
                //// both types are boxed, no need for conversion.
                var typeType = compiler.GetDot42InternalType("System", "Type").Resolve();
                var isInstanceOfType = typeType.Methods.Single(n => n.Name == "JavaIsInstance" && n.Parameters.Count == 1);
                var call = new AstExpression(node.SourceLocation, AstCode.Call, isInstanceOfType, loadExpr, node.Arguments[0]);
                node.CopyFrom(call);
            }

            // Expand newarr
            foreach (var node in ast.GetSelfAndChildrenRecursive<AstExpression>(x => x.Code == AstCode.Newarr))
            {
                var type = (XTypeReference)node.Operand;
                if (!type.IsDefinitionOrReferenceOrPrimitive())
                {
                    // Resolve type to a Class<?>
                    var typeHelperType = compiler.GetDot42InternalType(InternalConstants.TypeHelperName).Resolve();
                    // while having primitive arrays for primitive types would be nice, a lot of boxing and unboxing
                    // would be needed. only for-primitive-specialized generic classes we could optimize this.
                    var ldType = LoadTypeForGenericInstance(node.SourceLocation, currentMethod, type, compiler, typeHelperType, typeSystem, TypeConversion.EnsureRuntimeType);
                    var newInstanceExpr = new AstExpression(node.SourceLocation, AstCode.ArrayNewInstance, null, ldType, node.Arguments[0]) { ExpectedType = typeSystem.Object };
                    var arrayType = new XArrayType(type);
                    var cast = new AstExpression(node.SourceLocation, AstCode.SimpleCastclass, arrayType, newInstanceExpr) { ExpectedType = arrayType };
                    node.CopyFrom(cast);
                }
            }

            // Add generic instance call arguments
            foreach (var node in ast.GetSelfAndChildrenRecursive<AstExpression>(x => x.Code.IsCall()))
            {
                var method = (XMethodReference)node.Operand;
                if (method.DeclaringType.IsArray)
                    continue;
                XMethodDefinition methodDef;
                if (!method.TryResolve(out methodDef)) 
                    continue;
                if (methodDef.HasDexNativeAttribute())
                    continue;

                if (methodDef.NeedsGenericInstanceTypeParameter)
                {
                    // Add generic instance type parameter value
                    var arg = CreateGenericInstanceCallArguments(node.SourceLocation, method.DeclaringType, currentMethod, compiler);
                    node.Arguments.AddRange(arg);
                    node.GenericInstanceArgCount += arg.Count;
                }

                if (methodDef.NeedsGenericInstanceMethodParameter)
                {
                    // Add generic instance method parameter
                    var arg = CreateGenericInstanceCallArguments(node.SourceLocation, method, currentMethod, compiler);
                    node.Arguments.AddRange(arg);
                    node.GenericInstanceArgCount += arg.Count;
                }
            }

            // Add generic instance Delegate arguments for static methods.
            foreach (var node in ast.GetSelfAndChildrenRecursive<AstExpression>(x => x.Code == AstCode.Delegate))
            {
                var delegateInfo = (Tuple<XTypeDefinition, XMethodReference>)node.Operand;

                var genMethodDef = delegateInfo.Item2 as IXGenericInstance;
                var genTypeDef = delegateInfo.Item2.DeclaringType as IXGenericInstance;

                // Add generic instance type parameter value, if method is static
                if (genTypeDef != null && delegateInfo.Item2.Resolve().IsStatic)
                {
                    var arg = CreateGenericInstanceCallArguments(node.SourceLocation, delegateInfo.Item2.DeclaringType, currentMethod, compiler);
                    node.Arguments.AddRange(arg);
                    node.GenericInstanceArgCount += arg.Count;
                }

                // add generic method type parameter value.
                if (genMethodDef != null)
                {
                    var arg = CreateGenericInstanceCallArguments(node.SourceLocation, delegateInfo.Item2, currentMethod, compiler);
                    node.Arguments.AddRange(arg);
                    node.GenericInstanceArgCount += arg.Count;
                }
            }

            // Convert NewObj when needed
            foreach (var node in ast.GetSelfAndChildrenRecursive<AstExpression>(x => x.Code == AstCode.Newobj))
            {
                var ctorRef = (XMethodReference)node.Operand;
                var declaringType = ctorRef.DeclaringType;
                if (declaringType.IsArray)
                {
                    // New multi dimensional array
                    // Get element type
                    var elemType = ((XArrayType) declaringType).ElementType;
                    var typeExpr = new AstExpression(node.SourceLocation, AstCode.TypeOf, elemType);

                    // Create dimensions array
                    var intArrayType = new XArrayType(typeSystem.Int);
                    var dimArrayExpr = new AstExpression(node.SourceLocation, AstCode.InitArrayFromArguments, intArrayType, node.Arguments).SetType(intArrayType);

                    // Call java.lang.reflect.Array.newInstance(type, int[])
                    var newInstanceExpr = new AstExpression(node.SourceLocation, AstCode.ArrayNewInstance2, null, typeExpr, dimArrayExpr).SetType(typeSystem.Object);

                    // Cast to correct type
                    var cast = new AstExpression(node.SourceLocation, AstCode.SimpleCastclass, declaringType, newInstanceExpr).SetType(declaringType);

                    // Replace node
                    node.CopyFrom(cast);
                }
                else
                {
                    // Normal "new object"
                    XMethodDefinition ctorDef;
                    if (ctorRef.TryResolve(out ctorDef) && ctorDef.NeedsGenericInstanceTypeParameter)
                    {
                        // Add generic instance type parameter value
                        var arg = CreateGenericInstanceCallArguments(node.SourceLocation, ctorRef.DeclaringType, currentMethod, compiler);
                        node.Arguments.AddRange(arg);
                        node.GenericInstanceArgCount += arg.Count;
                    }
                }
            }
        }

        /// <summary>
        /// Build expressions that are used when calling a .NET method with generic instance parameters, one expression for
        /// each required parameter.
        /// </summary>
        private static IList<AstExpression> CreateGenericInstanceCallArguments(ISourceLocation seqp, XReference member, MethodSource currentMethod, AssemblyCompiler compiler)
        {
            // Prepare
            var genericInstance = member as IXGenericInstance;
            if (genericInstance == null)
            {
#if DEBUG
                //Debugger.Launch();
#endif
                throw new CompilerException(string.Format("{0} is not a generic instance", member));
            }
            var count = genericInstance.GenericArguments.Count;
            var typeHelperType = compiler.GetDot42InternalType(InternalConstants.TypeHelperName).Resolve();

            // Foreach type argument
            var typeExpressions = new List<AstExpression>();
            for (var i = 0; i < count; i++)
            {
                var argType = genericInstance.GenericArguments[i];
                var typeExpr = LoadTypeForGenericInstance(seqp, currentMethod, argType, compiler, typeHelperType, compiler.Module.TypeSystem, 
                                                          TypeConversion.EnsureTrueOrMarkerType);
                typeExpressions.Add(typeExpr);
            }

            bool isMethod = member is XMethodReference;
            bool buildArray = count > (isMethod ? InternalConstants.GenericMethodParametersAsArrayThreshold 
                                                : InternalConstants.GenericTypeParametersAsArrayThreshold);
            if (buildArray)
            {
                //TODO: when we can determine that all expressions just load our classes generic instance (array) field,
                //      and extract all values in sequence, we should shortcut and just return a load of the field,
                //      instead of unpacking and re-packing each value, putting pressure on the garbage collector
                //      in the process. This is not a huge problem any longer, as we do not use arrays as often
                //      as we used to. Nevertheless, where arrays are used, possible re-use should be faily common,
                //      e.g. when instatiating nested classes or invoking static methods.
                var elementType = compiler.Module.TypeSystem.Type;
                return new [] { new AstExpression(seqp, AstCode.InitArrayFromArguments, new XArrayType(elementType), typeExpressions) { ExpectedType = new XArrayType(elementType) }};
            }
            else
            {
                return typeExpressions;
            }
        }

        /// <summary>
        /// Create an expression that loads the given type at runtime.
        /// </summary>
        private static AstExpression LoadTypeForGenericInstance(ISourceLocation seqp, MethodSource currentMethod, 
                    XTypeReference type, AssemblyCompiler compiler, XTypeDefinition typeHelperType, XTypeSystem typeSystem, 
                    TypeConversion typeConversion, XGenericInstanceType typeGenericArguments=null)
        {
            if (type.IsArray)
            {
                // Array type
                var arrayType = (XArrayType)type;
                // Load element type
                var prefix = LoadTypeForGenericInstance(seqp, currentMethod, ((XArrayType)type).ElementType, compiler, typeHelperType, typeSystem, typeConversion);
                // Convert to array type
                if (arrayType.Dimensions.Count() == 1)
                {
                    var giCreateArray = typeHelperType.Methods.Single(x => (x.Name == "Array") && (x.Parameters.Count == 1));
                    return new AstExpression(seqp, AstCode.Call, giCreateArray, prefix) { ExpectedType = typeSystem.Type };
                }
                else
                {
                    var giCreateArray = typeHelperType.Methods.Single(x => (x.Name == "Array") && (x.Parameters.Count == 2));
                    var dimensionsExpr = new AstExpression(seqp, AstCode.Ldc_I4, arrayType.Dimensions.Count()) { ExpectedType = typeSystem.Int };
                    return new AstExpression(seqp, AstCode.Call, giCreateArray, prefix, dimensionsExpr) { ExpectedType = typeSystem.Type };
                }
            }

            var gp = type as XGenericParameter;
            if (gp != null)
            {
                AstExpression loadExpr;
                if (gp.Owner is XTypeReference)
                {
                    // Class type parameter
                    var owner = (XTypeReference)gp.Owner;
                    if (owner.GetElementType().Resolve().HasDexImportAttribute())
                    {
                        // Imported type
                        return new AstExpression(seqp, AstCode.TypeOf, typeSystem.Object) { ExpectedType = typeSystem.Type };
                    }
                    if (currentMethod.IsClassCtor)
                    {
                        // Class ctor's cannot have type information.
                        // Return Object instead
                        if(currentMethod.IsDotNet && !currentMethod.ILMethod.DeclaringType.HasSuppressMessageAttribute("StaticConstructorUsesGenericParameter"))
                        {
                            var msg = "Class (static) constructor of '{0}' tries to use generic parameter. This will always yield Object. " +
                                      "You can suppress this warning with a [SuppressMessage(\"dot42\", \"StaticConstructorUsesGenericParameter\")] " +
                                      "attribute on the class.";

                            if(seqp != null && seqp.Document != null)
                                DLog.Warning(DContext.CompilerCodeGenerator, seqp.Document, seqp.StartColumn,seqp.StartLine, msg, currentMethod.DeclaringTypeFullName);
                            else
                                DLog.Warning(DContext.CompilerCodeGenerator, msg, currentMethod.DeclaringTypeFullName);
                        }
                        return new AstExpression(seqp, AstCode.TypeOf, typeSystem.Object) { ExpectedType = typeSystem.Type };
                    }
                    loadExpr = currentMethod.IsStatic ?
                        LoadStaticClassGenericArgument(seqp, typeSystem, currentMethod.Method, gp.Position) :
                        LoadInstanceClassGenericArgument(seqp, typeSystem, currentMethod.Method.DeclaringType, gp.Position);
                }
                else
                {
                    // Method type parameter
                    var owner = (XMethodReference)gp.Owner;
                    if (owner.GetElementMethod().Resolve().DeclaringType.HasDexImportAttribute())
                    {
                        // Imported type
                        return LoadTypeForGenericInstance(seqp, currentMethod, type.Module.TypeSystem.Object, compiler, typeHelperType, typeSystem, typeConversion);
                    }
                    loadExpr = LoadMethodGenericArgument(seqp, typeSystem, currentMethod.Method, gp.Position);
                }

                if (typeConversion == TypeConversion.EnsureRuntimeType)
                    return EnsureGenericRuntimeType(loadExpr, typeSystem, typeHelperType);
                else
                    return loadExpr;
            }

            if (type is XTypeSpecification)
            {
                var typeSpec = (XTypeSpecification)type;
                var git = type as XGenericInstanceType;
                var baseType = LoadTypeForGenericInstance(seqp, currentMethod, typeSpec.ElementType, compiler, typeHelperType, typeSystem, typeConversion, git);

                if (typeConversion != TypeConversion.EnsureTrueOrMarkerType || typeSpec.GetElementType().IsNullableT())
                    return baseType;

                // Use the element type and make a generic proxy with the generic arguments.
                var parameters = CreateGenericInstanceCallArguments(seqp, git, currentMethod, compiler);
                if (parameters.Count == 1 && parameters[0].GetResultType().IsArray)
                {
                    // array type call.
                    var method = typeHelperType.Methods.Single(m => m.Name == "GetGenericInstanceType" && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.IsArray);
                    return new AstExpression(seqp, AstCode.Call, method, baseType, parameters[0]);
                }
                else
                {
                    parameters.Insert(0, baseType);
                    var method = typeHelperType.Methods.Single(m => m.Name == "GetGenericInstanceType" && m.Parameters.Count == parameters.Count && !m.Parameters[1].ParameterType.IsArray);
                    return new AstExpression(seqp, AstCode.Call, method, parameters.ToArray());
                }
            }


            if (typeConversion == TypeConversion.EnsureTrueOrMarkerType && type.GetElementType().IsNullableT())
            {
                if (typeGenericArguments != null)
                {
                    var underlying = typeGenericArguments.GenericArguments[0];
                    var code = underlying.IsPrimitive ? AstCode.BoxedTypeOf : AstCode.NullableTypeOf;
                    return new AstExpression(seqp, code, underlying) { ExpectedType = typeSystem.Type };
                }
                // if typeGenericArguments is null, this is a generic definition, e.g. typeof(Nullable<>).
            }

            // Plain type reference or definition
            return new AstExpression(seqp, AstCode.TypeOf, type) { ExpectedType = typeSystem.Type };
        }

        /// <summary>
        /// Expand the loadExpression, so that primitive types are converted to their boxed counterparts,
        /// and marker types are converted to their underlying types.
        /// </summary>
        private static AstExpression EnsureGenericRuntimeType(AstExpression loadExpr, XTypeSystem typeSystem, XTypeDefinition typeHelper)
        {
            var ensureMethod = typeHelper.Methods.Single(x => x.Name == "EnsureGenericRuntimeType");
            return new AstExpression(loadExpr.SourceLocation, AstCode.Call, ensureMethod, loadExpr)
                            .SetType(typeSystem.Type);
        }

        /// <summary>
        /// Load the GenericInstance of the current instance.
        /// The result is a temporary register.
        /// </summary>
        private static AstExpression LoadInstanceClassGenericArgument(ISourceLocation seqp, XTypeSystem typeSystem, XTypeDefinition type, int index)
        {
            bool loadFromArray = type.GenericParameters.Count > InternalConstants.GenericTypeParametersAsArrayThreshold;
            return LoadGenericArgument(seqp, typeSystem, AstCode.LdGenericInstanceField, index, loadFromArray);
        }

        /// <summary>
        /// Load the GenericInstance of the current static method.
        /// The result is a register that cannot be destroyed.
        /// </summary>
        private static AstExpression LoadStaticClassGenericArgument(ISourceLocation seqp, XTypeSystem typeSystem, XMethodDefinition method, int index)
        {
            bool loadFromArray = method.DeclaringType.GenericParameters.Count > InternalConstants.GenericTypeParametersAsArrayThreshold;
            return LoadGenericArgument(seqp, typeSystem, AstCode.LdGenericInstanceTypeArgument, index, loadFromArray);
        }

        /// <summary>
        /// Load the GenericInstance of the current generic method.
        /// The result is a register that cannot be destroyed.
        /// </summary>
        private static AstExpression LoadMethodGenericArgument(ISourceLocation seqp, XTypeSystem typeSystem, XMethodDefinition method, int index)
        {
            bool loadFromArray = method.GenericParameters.Count > InternalConstants.GenericMethodParametersAsArrayThreshold;
            return LoadGenericArgument(seqp, typeSystem, AstCode.LdGenericInstanceMethodArgument, index, loadFromArray);
        }

        private static AstExpression LoadGenericArgument(ISourceLocation seqp, XTypeSystem typeSystem, AstCode ldCode, int index, bool loadFromArray)
        {
            if (loadFromArray)
            {
                var getField = new AstExpression(seqp, ldCode, 0) { ExpectedType = new XArrayType(typeSystem.Type) };
                var indexExpr = new AstExpression(seqp, AstCode.Ldc_I4, index) { ExpectedType = typeSystem.Int };
                var loadExpr = new AstExpression(seqp, AstCode.Ldelem_Ref, null, getField, indexExpr) { ExpectedType = typeSystem.Type };
                return loadExpr;
            }
            else
            {
                return new AstExpression(seqp, ldCode, index) { ExpectedType = typeSystem.Type };
            }
        }
    }
}
