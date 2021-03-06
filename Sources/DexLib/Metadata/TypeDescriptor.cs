﻿using System.Text;

namespace Dot42.DexLib.Metadata
{
    public class TypeDescriptor
    {
        internal static TypeReference Allocate(string tdString)
        {
            if (!string.IsNullOrEmpty(tdString))
            {
                char prefix = tdString[0];
                var td = (TypeDescriptors) prefix;
                switch (td)
                {
                    case TypeDescriptors.Boolean:
                        return PrimitiveType.Boolean;
                    case TypeDescriptors.Byte:
                        return PrimitiveType.Byte;
                    case TypeDescriptors.Char:
                        return PrimitiveType.Char;
                    case TypeDescriptors.Double:
                        return PrimitiveType.Double;
                    case TypeDescriptors.Float:
                        return PrimitiveType.Float;
                    case TypeDescriptors.Int:
                        return PrimitiveType.Int;
                    case TypeDescriptors.Long:
                        return PrimitiveType.Long;
                    case TypeDescriptors.Short:
                        return PrimitiveType.Short;
                    case TypeDescriptors.Void:
                        return PrimitiveType.Void;
                    case TypeDescriptors.Array:
                        return new ArrayType();
                    case TypeDescriptors.FullyQualifiedName:
                        return new ClassReference();
                }
            }
            return null;
        }
        
        internal static void Fill(string tdString, TypeReference item)
        {
            if (!string.IsNullOrEmpty(tdString))
            {
                char prefix = tdString[0];
                var td = (TypeDescriptors) prefix;
                switch (td)
                {
                    case TypeDescriptors.Array:
                        var atype = (ArrayType) item;

                        TypeReference elementType = Allocate(tdString.Substring(1));
                        Fill(tdString.Substring(1), elementType);

                        /* All types are already allocated
                         * We want to reuse object reference if already in type repository
                         * BUT if not, we don't want to add a new reference to this type:
                         * it's a 'transient' type only used in the Dexer object model but
                         * not persisted in dex file.
                         */
                        atype.ElementType = elementType; //context.Import(elementType, false);

                        break;
                    case TypeDescriptors.FullyQualifiedName:
                        var cref = (ClassReference) item;
                        cref.Fullname = tdString.Substring(1, tdString.Length - 2);
                        break;
                }
            }
        }

        public static bool IsPrimitive(TypeDescriptors td)
        {
            return (td != TypeDescriptors.Array) && (td != TypeDescriptors.FullyQualifiedName);
        }

        public static string Encode(Prototype prototype)
        {
            var result = new StringBuilder();
            result.Append(Encode(prototype.ReturnType, true));

            foreach (Parameter parameter in prototype.Parameters)
                result.Append(Encode(parameter.Type, true));

            return result.ToString();
        }

        public static string Encode(TypeReference tref)
        {
            return Encode(tref, false);
        }

        public static string Encode(TypeReference tref, bool shorty)
        {
            string cached;
            if(!shorty && (cached = tref.CachedTypeDescriptor) != null)
                return cached;

            var result = new StringBuilder();

            var td = (char) tref.TypeDescriptor;

            if (!shorty)
            {
                result.Append(td);

                if (tref is ArrayType)
                    result.Append(Encode(((ArrayType) tref).ElementType, false));

                if (tref is ClassReference)
                {
                    result.Append(((ClassReference) tref).Fullname.Replace(ClassReference.NamespaceSeparator,
                                                          ClassReference.InternalNamespaceSeparator));
                    result.Append(';');
                }
            }
            else
            {
                /* A ShortyDescriptor is the short form representation of a method prototype, 
                 * including return and parameter types, except that there is no distinction
                 * between various reference (class or array) types. Instead, all reference
                 * types are represented by a single 'L' character. */
                if (td == (char) TypeDescriptors.Array)
                    td = (char) TypeDescriptors.FullyQualifiedName;

                result.Append(td);
            }

            if (!shorty)
            {
                cached = result.ToString();
                tref.CachedTypeDescriptor = cached;
                return cached;
            }

            return result.ToString();
        }

        public static object GetDefaultValue(TypeReference tref)
        {
            switch (tref.TypeDescriptor)
            {
                case TypeDescriptors.Boolean:
                    return false;
                case TypeDescriptors.Byte:
                    return (sbyte) 0;
                case TypeDescriptors.Char:
                    return (char)0;
                case TypeDescriptors.Double:
                    return (double)0;
                case TypeDescriptors.Float:
                    return (float)0;
                case TypeDescriptors.Int:
                    return (int)0;
                case TypeDescriptors.Long:
                    return (long)0;
                case TypeDescriptors.Short:
                    return (short)0;
            }
            return null;
        }
    }
}