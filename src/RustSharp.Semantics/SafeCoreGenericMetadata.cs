using System.Diagnostics;
using System.Text.Json;
using RustSharp.Syntax;

namespace RustSharp.Semantics;

/// <summary>Versioned source-linked generic templates and closed reachability, embedded by the CLR backend.</summary>
public static class SafeCoreGenericMetadata
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 8 * 1024 * 1024;

    public static byte[] Encode(SafeCoreGenericAnalysisProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        int work = 0;
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("profile", SafeCoreGenericAnalysis.Profile);
        writer.WriteString("traitProfile", "bounded-traits-v1");
        writer.WriteString("linkage", "source-linked-crates");
        writer.WriteStartArray("crates");
        foreach (SafeCoreCrate crate in program.Hir.NameResolution!.Crates.OrderBy(static value => value.ScopePath, StringComparer.Ordinal))
        {
            Step();
            writer.WriteStartObject();
            writer.WriteString("scope", crate.ScopePath);
            writer.WriteString("identity", crate.Identity);
            writer.WriteStartObject("dependencies");
            foreach (var dependency in crate.Dependencies.OrderBy(static value => value.Key, StringComparer.Ordinal))
            {
                Step();
                writer.WriteString(dependency.Key, dependency.Value);
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("hir");
        foreach (SafeCoreHirNode node in program.Hir.Nodes)
        {
            Step();
            writer.WriteStartObject();
            writer.WriteNumber("id", node.Id);
            writer.WriteString("kind", node.Kind.ToString());
            writer.WriteNumber("start", node.Span.Start);
            writer.WriteNumber("length", node.Span.Length);
            writer.WriteNumber("modifiers", (int)node.Modifiers);
            if (node.Name is not null) writer.WriteString("name", node.Name);
            if (node.Value is not null) writer.WriteString("value", node.Value);
            WriteSymbol("declaration", node.DeclaredSymbol);
            WriteSymbol("reference", node.ReferencedSymbol);
            writer.WriteStartArray("children");
            foreach (int child in node.ChildIds) { Step(); writer.WriteNumberValue(child); }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("nominalTypes");
        foreach (SafeCoreGenericNominalDefinition nominal in program.NominalTypes)
        {
            Step();
            writer.WriteStartObject(); writer.WriteString("id", nominal.Id);
            writer.WriteNumber("declaration", nominal.Declaration.Id);
            writer.WriteStartArray("parameters");
            foreach (string parameter in nominal.Parameters) writer.WriteStringValue(parameter);
            writer.WriteEndArray();
            writer.WriteStartArray("fields");
            foreach (SafeCoreGenericFieldDefinition field in nominal.Fields)
            {
                Step(); writer.WriteStartObject(); writer.WriteString("name", field.Name);
                writer.WriteNumber("declaration", field.Declaration.Id);
                writer.WritePropertyName("type"); WriteType(field.Type, 0); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("functions");
        foreach (SafeCoreGenericFunctionDefinition function in program.Functions)
        {
            Step();
            writer.WriteStartObject(); writer.WriteString("id", function.Id);
            writer.WriteNumber("declaration", function.Declaration.Id); writer.WriteNumber("body", function.Body.Id);
            writer.WriteStartArray("parameters");
            foreach (string parameter in function.PlanDefinition.Parameters) writer.WriteStringValue(parameter);
            writer.WriteEndArray();
            writer.WriteStartArray("parameterTypes");
            foreach (RustType type in function.PlanDefinition.ParameterTypes) WriteType(type, 0);
            writer.WriteEndArray(); writer.WritePropertyName("returnType"); WriteType(function.PlanDefinition.ReturnType, 0);
            writer.WriteStartArray("bounds");
            foreach (GenericTraitObligation bound in function.PlanDefinition.Bounds)
            {
                Step(); writer.WriteStartObject(); writer.WriteString("trait", bound.Trait);
                writer.WritePropertyName("target"); WriteType(bound.Target, 0); writer.WriteEndObject();
            }
            writer.WriteEndArray(); WriteEvidence(function.Types, function.Calls); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("instances");
        foreach (SafeCoreGenericSpecialization specialization in program.Specializations)
        {
            Step(); writer.WriteStartObject(); writer.WritePropertyName("instance"); WriteCall(specialization.Instance);
            WriteEvidence(specialization.Types, specialization.Calls); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("selectedImplementations");
        foreach (string implementation in program.Plan.SelectedImplementations) { Step(); writer.WriteStringValue(implementation); }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); Step();
        return stream.ToArray();

        void WriteSymbol(string property, SafeCoreSymbol? symbol)
        {
            if (symbol is null) return;
            Step(); writer.WriteStartObject(property); writer.WriteString("id", symbol.QualifiedName);
            writer.WriteString("kind", symbol.Kind.ToString()); writer.WriteString("scope", symbol.ScopePath);
            writer.WriteString("visibility", symbol.VisibilityScopePath);
            writer.WriteString("target", symbol.ResolvedImportTargetQualifiedName);
            writer.WriteEndObject();
        }
        void WriteEvidence(IReadOnlyDictionary<int, RustType> types, IReadOnlyDictionary<int, GenericFunctionInstance> calls)
        {
            writer.WriteStartArray("types");
            foreach (var entry in types.OrderBy(static item => item.Key))
            {
                Step(); writer.WriteStartObject(); writer.WriteNumber("node", entry.Key);
                writer.WritePropertyName("type"); WriteType(entry.Value, 0); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteStartArray("calls");
            foreach (var entry in calls.OrderBy(static item => item.Key))
            {
                Step(); writer.WriteStartObject(); writer.WriteNumber("node", entry.Key);
                writer.WritePropertyName("target"); WriteCall(entry.Value); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        void WriteCall(GenericFunctionInstance instance)
        {
            Step(); writer.WriteStartObject(); writer.WriteString("function", instance.FunctionId);
            writer.WriteStartArray("arguments");
            foreach (RustType argument in instance.Arguments) WriteType(argument, 0);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        void WriteType(RustType type, int depth)
        {
            Step();
            if (depth > 128) throw new TimeoutException("Generic metadata type nesting exceeds its limit.");
            writer.WriteStartObject(); writer.WriteString("kind", type.Kind.ToString()); writer.WriteString("name", type.Name);
            writer.WriteStartArray("arguments");
            foreach (RustType argument in type.Arguments) WriteType(argument, depth + 1);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++work > 1_000_000 || writer.BytesCommitted + writer.BytesPending > MaximumBytes || clock.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Generic metadata exceeded its byte, work or time limit.");
        }
    }
}
