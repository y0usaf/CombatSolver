using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombatSolver;

// Only prepares the next process. Never treats an edited file as evidence that
// the current CLR has switched GC, and never writes through a game-file hardlink.
internal static class RuntimeGcStartupConfig
{
    internal const string ProfileKey = "CombatSolver.RuntimeProfile";
    internal const string PreviousKey = "CombatSolver.PreviousServerGc";
    internal const string PreviousAdaptationKey = "CombatSolver.PreviousDynamicAdaptationMode";
    private const string ServerKey = "System.GC.Server";
    private const string AdaptationKey = "System.GC.DynamicAdaptationMode";

    internal static string Apply(string path, bool enabled)
    {
        byte[] original = File.ReadAllBytes(path);
        JsonObject document = JsonNode.Parse(original) as JsonObject
            ?? throw new InvalidDataException("Empty runtime configuration.");
        if (document["runtimeOptions"] is not JsonObject options)
            throw new InvalidDataException("Missing runtimeOptions.");
        JsonObject properties;
        if (options["configProperties"] is null)
        {
            if (!enabled) return "Unchanged";
            properties = new JsonObject();
            options["configProperties"] = properties;
        }
        else if (options["configProperties"] is JsonObject existing)
            properties = existing;
        else
            throw new InvalidDataException("Invalid configProperties.");

        bool owned = properties.ContainsKey(ProfileKey) || properties.ContainsKey(PreviousKey);
        string? previous = null;
        if (owned)
        {
            if (properties[ProfileKey] is not JsonValue profile
                || !profile.TryGetValue<string>(out string? value)
                || value != RuntimeGcProfile.ServerGenerational
                || properties[PreviousKey] is not JsonValue saved
                || !saved.TryGetValue<string>(out previous)
                || previous is not ("absent" or "true" or "false"))
                throw new InvalidDataException("Unknown or incomplete GC configuration ownership.");
            if (properties[ServerKey] is not JsonValue current
                || !current.TryGetValue<bool>(out bool server) || !server)
                throw new InvalidDataException("Owned GC configuration was changed externally.");
            string? previousAdaptation = null;
            if (properties.ContainsKey(PreviousAdaptationKey))
            {
                if (properties[PreviousAdaptationKey] is not JsonValue savedAdaptation
                    || !savedAdaptation.TryGetValue(out previousAdaptation)
                    || (previousAdaptation != "absent" && !int.TryParse(previousAdaptation, out _)))
                    throw new InvalidDataException("Unknown or incomplete GC configuration ownership.");
                if (properties[AdaptationKey] is not JsonValue adaptation
                    || !adaptation.TryGetValue(out int mode) || mode != 0)
                    throw new InvalidDataException("Owned GC configuration was changed externally.");
                if (enabled) return "AlreadyConfigured";
            }
            else if (enabled)
            {
                properties[PreviousAdaptationKey] = PreviousAdaptation(properties);
                properties[AdaptationKey] = 0;
            }
            if (!enabled)
            {
                if (previous == "absent") properties.Remove(ServerKey);
                else properties[ServerKey] = previous == "true";
                if (previousAdaptation == "absent") properties.Remove(AdaptationKey);
                else if (previousAdaptation != null) properties[AdaptationKey] = int.Parse(previousAdaptation);
                properties.Remove(ProfileKey);
                properties.Remove(PreviousKey);
                properties.Remove(PreviousAdaptationKey);
            }
        }
        else
        {
            if (!enabled) return "Unchanged";
            previous = "absent";
            if (properties.ContainsKey(ServerKey))
            {
                if (properties[ServerKey] is not JsonValue current
                    || !current.TryGetValue<bool>(out bool server))
                    throw new InvalidDataException("Invalid System.GC.Server value.");
                previous = server ? "true" : "false";
            }
            properties[PreviousAdaptationKey] = PreviousAdaptation(properties);
            properties[ServerKey] = true;
            properties[AdaptationKey] = 0;
            properties[ProfileKey] = RuntimeGcProfile.ServerGenerational;
            properties[PreviousKey] = previous;
        }

        // Same-directory rename is atomic; a crash cannot leave half a JSON file.
        // Preserve unrelated fields and decline a detected concurrent edit.
        string temporary = path + ".combatsolver-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document,
                new JsonSerializerOptions { WriteIndented = true });
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new IOException("Runtime configuration changed while preparing GC mode.");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return enabled ? owned ? "Upgraded" : "Prepared" : "Restored";
    }

    private static string PreviousAdaptation(JsonObject properties)
    {
        if (!properties.ContainsKey(AdaptationKey))
            return "absent";
        if (properties[AdaptationKey] is not JsonValue current || !current.TryGetValue(out int mode))
            throw new InvalidDataException("Invalid System.GC.DynamicAdaptationMode value.");
        return mode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
