using Newtonsoft.Json.Serialization;

namespace Cruncharr.API;

public class PreserveDictionaryKeysContractResolver : CamelCasePropertyNamesContractResolver
{
    protected override string ResolveDictionaryKey(string dictionaryKey)
    {
        // Preserve dictionary keys as-is (don't camelCase them)
        return dictionaryKey;
    }
}
