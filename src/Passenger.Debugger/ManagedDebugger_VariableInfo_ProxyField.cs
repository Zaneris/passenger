using Microsoft.CodeAnalysis.CSharp.Symbols;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private static ICorDebugValue? GetAsyncOrLambdaProxyFieldValue(ICorDebugValue compilerGeneratedClassValue, IMetaDataImport metadataImport)
    {
        ICorDebugObjectValue objectValue = compilerGeneratedClassValue.UnwrapDebugValueToObject();
        IEnumerable<mdFieldDef> fields = metadataImport.EnumFields(objectValue.Class.Token);
        foreach (mdFieldDef field in fields)
        {
            (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(field);
            string fieldName = fieldProps.szField;
            GeneratedNameKind generatedNameKind = GeneratedNameParser.GetKind(fieldName);
            if (generatedNameKind is GeneratedNameKind.ThisProxyField)
            {
                ICorDebugValue fieldCorDebugValue = objectValue.GetFieldValue(objectValue.Class, field);
                return fieldCorDebugValue;
            }
            else if (generatedNameKind is GeneratedNameKind.DisplayClassLocalOrField)
            {
                // This field points to a parent closure class - follow the chain to find 'this'
                ICorDebugValue parentClosureValue = objectValue.GetFieldValue(objectValue.Class, field);
                if (parentClosureValue.UnwrapDebugValueToObjectOrNull() is not { } parentObjectValue)
                {
                    // A closure of a scope the code has not reached yet is null, holding no 'this'.
                    continue;
                }
                IMetaDataImport parentMetadataImport = parentObjectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
                return GetAsyncOrLambdaProxyFieldValue(parentClosureValue, parentMetadataImport);
            }
        }

        // E.g. in a static async method, there is no 'this' proxy field.
        return null;
    }
}
