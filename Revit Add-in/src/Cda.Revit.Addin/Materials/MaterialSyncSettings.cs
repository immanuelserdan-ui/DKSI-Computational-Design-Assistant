using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Materials;

/// <summary>How a layer is chosen when a type has a compound structure.</summary>
public enum LayerRule
{
    /// <summary>
    /// Pick the layer whose MATERIAL actually carries classification data, thickest
    /// wins if several do. This is the rule that matches how the models are authored -
    /// the classified material is typically a thin finish layer, so the geometric rules
    /// below look at the wrong layer and find nothing.
    /// </summary>
    Classified,

    Structural,
    Thickest,
    Exterior,
    Interior,
}

/// <summary>One material identity field and the two parameters it feeds.</summary>
public sealed record MaterialMapping(
    BuiltInParameter SourceBip,
    string[] SourceNames,
    string Target,
    string InstanceTarget,
    /// <summary>True when this field should hold the bk.* style code.</summary>
    bool IsCode);

/// <summary>
/// The config block from MaterialToTypeParams.py, as settings rather than module
/// constants so a dialog can expose any of it later.
/// </summary>
public sealed class MaterialSyncSettings
{
    public static readonly MaterialMapping[] DefaultMapping =
    [
        new(BuiltInParameter.ALL_MODEL_MANUFACTURER,
            ["Manufacturer", "Producent", "Fabrikant"],
            "FK Kode", "FK Kode Instance", IsCode: true),

        new(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
            ["Comments", "Kommentarer"],
            "FM Bygningsdel", "FM Bygningsdel Instance", IsCode: false),
    ];

    public IReadOnlyList<MaterialMapping> Mapping { get; init; } = DefaultMapping;

    /// <summary>
    /// Strings that are never classification data. Revit writes the first of these into
    /// a material's Comments when a model upgrade cannot migrate its appearance asset;
    /// it sits on dozens of library materials (Chrome, Plastic Dark Gray, ...).
    /// </summary>
    public IReadOnlyList<string> IgnoreValues { get; init; } = ["rendering appearance not upgraded"];

    /// <summary>
    /// A material counts as classified only when one of its two identity fields matches
    /// this - the FK code shape, e.g. bk.fun / bk.vaeg. Empty accepts any non-empty
    /// value. Without it a whole-model run copies junk: measured on this project, 173 of
    /// 969 types held "a value" but only about 2 were real classifications.
    /// </summary>
    public string CodePattern { get; init; } = @"^[A-Za-zÆØÅæøå]{2,4}\.";

    /// <summary>
    /// Some materials were filled in the other way round - code in Comments, name in
    /// Manufacturer. True detects and corrects that while writing.
    /// </summary>
    public bool AutoFixSwapped { get; init; } = true;

    /// <summary>Categories that can never carry a material. Excluded for speed.</summary>
    public IReadOnlyList<BuiltInCategory> ExcludedCategories { get; init; } =
    [
        BuiltInCategory.OST_Rooms,
        BuiltInCategory.OST_Areas,
        BuiltInCategory.OST_MEPSpaces,
        BuiltInCategory.OST_RvtLinks,
        BuiltInCategory.OST_IOSModelGroups,
        BuiltInCategory.OST_Levels,
        BuiltInCategory.OST_Grids,
        BuiltInCategory.OST_SectionBox,
        BuiltInCategory.OST_Cameras,
    ];

    /// <summary>
    /// Settings and 2D content that can never carry a building-part classification.
    /// These made up most of the 705 "no material" rows on the reference project.
    /// </summary>
    public IReadOnlyList<string> ExcludedCategoryNames { get; init; } =
    [
        "Pipe Materials", "Pipe Schedules", "Pipe Connections", "Pipe Segments",
        "Wire Materials", "Wire Insulations", "Wire Temperature Ratings",
        "Fluids", "Conduit Standards", "Voltages", "Distribution Systems",
        "Duct Systems", "Piping Systems", "Cable Tray Settings",
        "Cover Type", "Constructions", "Cut Marks",
        "Profiles", "Detail Items", "Property Lines",
        "Mass Walls", "Mass Roof", "Mass Floors", "Mass Shade", "Mass Opening",
        "Mass Windows and Skylights", "Mass Exterior Wall", "Mass Interior Wall",
        "Mass Glazing", "Mass Zone", "Analytical Spaces",
    ];

    /// <summary>Null = every model category; otherwise only these.</summary>
    public IReadOnlyList<string>? CategoryNames { get; init; }

    public LayerRule LayerRule { get; init; } = LayerRule.Classified;

    /// <summary>
    /// An instance may override its type's material via an instance-level Material
    /// parameter. True classifies each instance by its own material.
    /// </summary>
    public bool PerInstanceMaterial { get; init; } = true;

    /// <summary>Also write the "... Instance" parameters.</summary>
    public bool WriteInstances { get; init; } = true;

    /// <summary>Re-stamp values that are already filled in.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Report every type, including ones skipped because their material has no
    /// classification data. Those rows are exactly what explains "nothing happened".
    /// </summary>
    public bool ReportAll { get; init; } = true;

    public int MaxUnresolved { get; init; } = 300;
}
