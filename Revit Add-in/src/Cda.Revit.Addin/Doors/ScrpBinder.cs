using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Doors;

/// <summary>
/// Binds SCRP shared parameters that already exist in a document to the Doors category.
///
/// WHY THIS IS SAFE, AND WHY IT WOULD NOT HAVE BEEN
///   Creating these parameters from nothing was refused, and rightly: they are an office
///   standard, and a parameter minted here would carry a NEW GUID. It would look correct,
///   schedule correctly in this model, and line up with nothing in any project that already
///   uses the real ones - a mismatch nobody notices until two models disagree.
///
///   This does something different. The definitions are ALREADY in the document, carrying the
///   office GUIDs; they are simply not bound to Doors. Measured in FM_Template 2027V1.00_EN:
///   all four present, none bound, so every door was skipped and the resolver wrote nothing.
///   Binding an existing definition reuses its GUID by construction - there is no new
///   identity, so nothing downstream can drift.
///
///   A definition that is NOT in the document is still refused. That case needs the office
///   shared parameter file, and this class says so rather than inventing one.
/// </summary>
internal sealed class ScrpBinder
{
    private readonly Document _doc;

    public ScrpBinder(Document doc) => _doc = doc;

    /// <summary>What a document currently offers for one parameter name.</summary>
    /// <param name="BoundToDoors">Bound to the Doors category, by either kind of binding.</param>
    /// <param name="TypeBoundToDoors">
    /// Bound to Doors as a TYPE parameter, which is a problem this class cannot fix and used to
    /// hide. See <see cref="Bindable"/>.
    /// </param>
    public sealed record Status(string Name, bool InDocument, bool BoundToDoors, bool TypeBoundToDoors)
    {
        /// <summary>Present but not on Doors - the one case this class can fix.</summary>
        public bool Bindable => InDocument && !BoundToDoors;

        /// <summary>
        /// Bound, but to the wrong kind of binding - present on Doors as a TYPE parameter.
        ///
        /// THIS USED TO BE INVISIBLE, and it made the whole command silently useless on such a
        /// model. Every write goes through ParameterHelper.Find, which searches instance
        /// parameters only, so a type-bound SCRP parameter is never found and the resolver
        /// writes nothing - while Survey reported it as correctly bound, so nothing offered to
        /// fix it and the run reported success. The door schedules kept the previous values and
        /// the only symptom was "the automation is not running".
        ///
        /// It is deliberately NOT auto-repaired. Rebinding type to instance discards whatever
        /// values the type binding holds, and doing that to office shared parameters without
        /// being asked is not this tool's call. It is reported instead, with the reason.
        /// </summary>
        public bool NeedsRebinding => InDocument && TypeBoundToDoors;
    }

    public sealed record Outcome(string Name, bool Success, string Detail);

    /// <summary>Read-only survey. Safe to call outside a transaction.</summary>
    public IReadOnlyList<Status> Survey(IReadOnlyList<string> names)
    {
        var shared = SharedDefinitions();
        var bindings = Bindings();
        var doors = DoorCategoryId();

        var result = new List<Status>();

        foreach (var name in names)
        {
            var key = ParameterHelper.Squash(name);
            var inDocument = shared.ContainsKey(key);

            var bound = false;
            var typeBound = false;

            if (bindings.TryGetValue(key, out var entry) && doors is not null)
            {
                try
                {
                    foreach (Category category in entry.Binding.Categories)
                        if (category.Id == doors) { bound = true; break; }

                    // The KIND of binding matters as much as its presence. An instance binding
                    // is what the resolver can write to; a type binding is not, and reporting
                    // the two alike is what let a type-bound model look correctly configured
                    // while nothing was ever written. See Status.NeedsRebinding.
                    if (bound && entry.Binding is TypeBinding) typeBound = true;
                }
                catch
                {
                    // An unreadable category set cannot confirm the binding; treat as unbound
                    // so the caller is offered the fix rather than told everything is fine.
                }
            }

            result.Add(new Status(name, inDocument, bound, typeBound));
        }

        return result;
    }

    /// <summary>
    /// Binds each name to Doors, keeping whatever categories it was already bound to.
    /// Caller owns the transaction.
    /// </summary>
    public IReadOnlyList<Outcome> Bind(IReadOnlyList<string> names)
    {
        var outcomes = new List<Outcome>();

        var doors = DoorCategory();
        if (doors is null)
        {
            foreach (var name in names)
                outcomes.Add(new Outcome(name, false, "the Doors category does not accept bound parameters."));

            return outcomes;
        }

        var shared = SharedDefinitions();

        // READ THE WHOLE MAP BEFORE TOUCHING IT. Revit's BindingMap cannot be modified while
        // an iterator over it is still open, and the failure is silent - ReInsert simply
        // returns false. FinishParameterSetup carries the same warning for the same reason;
        // that bug meant widening a parameter had never once worked.
        var bindings = Bindings();

        foreach (var name in names)
        {
            var key = ParameterHelper.Squash(name);

            if (!shared.TryGetValue(key, out var element))
            {
                outcomes.Add(new Outcome(name,
                    false,
                    "not present in this document - it has to come from the office shared " +
                    "parameter file, so that its GUID matches every other model."));
                continue;
            }

            try
            {
                outcomes.Add(bindings.TryGetValue(key, out var existing)
                    ? Widen(name, existing.Definition, existing.Binding, doors)
                    : Insert(name, element, doors));
            }
            catch (Exception ex)
            {
                outcomes.Add(new Outcome(name, false, ex.Message));
            }
        }

        return outcomes;
    }

    /// <summary>Adds Doors to a parameter that is already bound to something else.</summary>
    private Outcome Widen(string name, Definition definition, ElementBinding binding, Category doors)
    {
        var set = _doc.Application.Create.NewCategorySet();
        var already = false;

        foreach (Category category in binding.Categories)
        {
            set.Insert(category);
            if (category.Id == doors.Id) already = true;
        }

        if (already) return new Outcome(name, true, "already bound to Doors - left alone.");

        // A TYPE binding widened to Doors is still a type binding, and the resolver cannot write
        // to one - ParameterHelper.Find searches instance parameters only. Adding Doors to it
        // would report success and produce a column that stays empty forever, which is the
        // failure this whole prompt exists to prevent rather than to create by another route.
        if (binding is TypeBinding)
        {
            return new Outcome(name, false,
                "it is bound to other categories as a TYPE parameter, and widening it would bind " +
                "Doors the same way. Two doors of the same type face different rooms, so this " +
                "has to be an INSTANCE binding. Rebind it in Manage > Project Parameters - not " +
                "done here, because that discards the values the type binding holds.");
        }

        set.Insert(doors);

        // Instance, and only instance - the type case returned above rather than being widened
        // into a binding this add-in cannot write through.
        var rebuilt = _doc.Application.Create.NewInstanceBinding(set);

        return _doc.ParameterBindings.ReInsert(definition, rebuilt, GroupTypeId.Data)
            ? new Outcome(name, true, "Doors added to its existing categories.")
            : new Outcome(name, false, "Revit refused the re-binding.");
    }

    /// <summary>Binds a definition that exists in the document but is bound to nothing.</summary>
    private Outcome Insert(string name, SharedParameterElement element, Category doors)
    {
        var definition = element.GetDefinition();
        if (definition is null) return new Outcome(name, false, "its definition could not be read.");

        var set = _doc.Application.Create.NewCategorySet();
        set.Insert(doors);

        // Instance, not type: two doors in the same wall type face different rooms, so a type
        // binding would give them one shared value and make the whole exercise meaningless.
        return _doc.ParameterBindings.Insert(
                   definition, _doc.Application.Create.NewInstanceBinding(set), GroupTypeId.Data)
            ? new Outcome(name, true, $"bound to Doors (instance), GUID {element.GuidValue}.")
            : new Outcome(name, false, "Revit refused the binding.");
    }

    // ------------------------------------------------------------------ lookups

    private Dictionary<string, SharedParameterElement> SharedDefinitions()
    {
        var found = new Dictionary<string, SharedParameterElement>();

        try
        {
            foreach (var element in new FilteredElementCollector(_doc)
                         .OfClass(typeof(SharedParameterElement)))
            {
                try
                {
                    if (element is not SharedParameterElement shared) continue;

                    var key = ParameterHelper.Squash(shared.Name);
                    if (key.Length > 0) found.TryAdd(key, shared);
                }
                catch
                {
                    // Unreadable definition - nothing to offer for it.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"SCRP binder: could not enumerate shared parameters: {ex.Message}");
        }

        return found;
    }

    /// <summary>
    /// Every binding in the document, keyed by squashed name, read ONCE and to completion.
    /// See the note in <see cref="Bind"/> for why the iterator must never be abandoned early.
    /// </summary>
    private Dictionary<string, (Definition Definition, ElementBinding Binding)> Bindings()
    {
        var found = new Dictionary<string, (Definition, ElementBinding)>();

        try
        {
            var iterator = _doc.ParameterBindings.ForwardIterator();

            // No early exit anywhere in this loop.
            while (iterator.MoveNext())
            {
                try
                {
                    if (iterator.Key is { } definition && iterator.Current is ElementBinding binding)
                        found.TryAdd(ParameterHelper.Squash(definition.Name), (definition, binding));
                }
                catch
                {
                    // A binding whose definition cannot be read cannot be matched anyway.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"SCRP binder: could not read the binding map: {ex.Message}");
        }

        return found;
    }

    private Category? DoorCategory()
    {
        try
        {
            var category = Category.GetCategory(_doc, BuiltInCategory.OST_Doors);
            return category is { AllowsBoundParameters: true } ? category : null;
        }
        catch
        {
            return null;
        }
    }

    private ElementId? DoorCategoryId()
    {
        try { return Category.GetCategory(_doc, BuiltInCategory.OST_Doors)?.Id; }
        catch { return null; }
    }
}
