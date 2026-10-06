using System.Text.Json.Serialization;

namespace RustSharp.Tests;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RegressionHarness.HarnessReport))]
[JsonSerializable(typeof(RegressionHarness.RegistrationInventory))]
internal sealed partial class RegressionHarnessJsonContext : JsonSerializerContext;
