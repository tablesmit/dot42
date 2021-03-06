﻿using System;
using System.Text;
using Dot42.DexLib;
using Dot42.DexLib.Metadata;

namespace Dot42.CompilerLib.RL
{
    public class Catch : ICloneable, IEquatable<Catch>
    {
        public TypeReference Type { get; set; }
        public Instruction Instruction { get; set; }

        /// <summary>
        /// Default ctor
        /// </summary>
        public Catch()
        {            
        }

        /// <summary>
        /// Copy ctor
        /// </summary>
        public Catch(Catch source)
        {
            Type = source.Type;
            Instruction = source.Instruction;
        }

        object ICloneable.Clone()
        {
            var result = new Catch();

            result.Type = Type;
            result.Instruction = Instruction;

            return result;
        }

        internal Catch Clone()
        {
            return (Catch) (this as ICloneable).Clone();
        }

        public bool Equals(Catch other)
        {
            return Type.Equals(other.Type)
                   && Instruction.Equals(other.Instruction);
        }

        public override bool Equals(object obj)
        {
            if (obj is Catch)
                return Equals(obj as Catch);

            return false;
        }

        public override int GetHashCode()
        {
            var builder = new StringBuilder();

            builder.AppendLine(TypeDescriptor.Encode(Type));
            builder.AppendLine(Instruction.GetHashCode().ToString());

            return builder.ToString().GetHashCode();
        }
    }
}