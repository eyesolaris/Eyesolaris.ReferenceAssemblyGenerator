using dnlib.DotNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eyesolaris.ReferenceAssemblyGenerator
{
    internal static class SignatureHelper
    {
        internal static string GetSignature(MethodDef method, IReadOnlyList<TypeSig> genericArguments)
        {
            TypeSig returnType = method.ReturnType;
            ParamDef[] parameters = method.ParamDefs.ToArray();
            IReadOnlyList<TypeSig> enclosingTypeArguments;
            IReadOnlyList<TypeSig> methodArguments;
            TypeDef enclosingType = method.DeclaringType;
            if (enclosingType.HasGenericParameters)
            {
                int typeParametersCount = enclosingType.GenericParameters.Count;
                enclosingTypeArguments = genericArguments.Take(typeParametersCount).ToArray();
                methodArguments = genericArguments.Skip(typeParametersCount).ToArray();
            }
            else
            {
                enclosingTypeArguments = [];
                methodArguments = genericArguments;
            }
            throw new NotImplementedException();
        }
    }
}
