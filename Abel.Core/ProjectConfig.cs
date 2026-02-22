using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Abel.Core;

/*
{
	"name": "math_module",
	"cxx_standard": 23,
	"sources": {
		"modules": ["src/math.cppm"],
		"private": ["src/math_impl.cpp"]
	},
	"dependencies": [],
	"tests": {
		"files": ["tests/math_module_consumer.cpp"]
	}
}

*/

public class TestsConfig
{
    [JsonPropertyName("files")]
    public IList<string> Files { get; set; } = new List<string>();
}

public class ProjectConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // 2. Changed to 'int' to safely accept the unquoted JSON number (23)
    [JsonPropertyName("cxx_standard")]
    public int CXXStandard { get; set; } = 23;

    [JsonPropertyName("output_type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OutputType ProjectOutputType { get; set; } = OutputType.library;

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    [JsonPropertyName("sources")]
    public IDictionary<string, string[]> Sources { get; }

    [JsonPropertyName("dependencies")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public IList<string> Dependencies { get; }

    [JsonPropertyName("tests")]
    public TestsConfig Tests { get; set; }

    public ProjectConfig()
    {
        Dependencies = new List<string>();
        Tests = new TestsConfig();

        // System.Text.Json is smart enough to populate this pre-initialized dictionary!
        if (OperatingSystem.IsLinux())
            Sources = new Dictionary<string, string[]>(StringComparer.Ordinal);
        else
            Sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }
}
