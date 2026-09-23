using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

using Broiler.JavaScript.Ast;
using Broiler.JavaScript.Ast.Expressions;
using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.Ast.Statements;
using Broiler.JavaScript.ExpressionCompiler.Core;
using Broiler.JavaScript.Parser;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// What the module adapter needs to know about one module's text before the engine runs it: its
/// static requests, and whether it calls <c>import()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not linking; the engine links.</b> Broiler.JS 0.1.0-preview.3 loads, links and
/// evaluates a module graph as ECMAScript specifies, resolving every request through
/// <c>JSModuleContext.Resolve</c>. What JSEAL reads here is the module's requested modules, which the
/// contract needs before evaluation: the map resolves and loads each through the host, so that a
/// missing module fails through <see cref="IJsModuleMap.LoadAsync"/> rather than in the middle of an
/// evaluation, and the engine's <c>Resolve</c> is then answered from what the host said.
/// </para>
/// <para>
/// <b>One refusal is left, and it is the adapter's, not the engine's.</b> The engine resolves an
/// <c>import()</c> through the same <c>Resolve</c>, at run time, with a specifier the map has never
/// seen; the adapter cannot answer that synchronously from the host, so a module that calls
/// <c>import()</c> is refused at link time rather than left to reject with an engine error (I12).
/// The check is syntactic and walks every node, so it can only refuse more than it must.
/// </para>
/// </remarks>
internal sealed class BroilerJsModuleAnalysis
{
    /// <summary>
    /// The engine's parse-goal switches. Without both, a standalone parse of module text would use the
    /// script goal and misread a top-level <c>await</c>. Looked up by reflection whatever their
    /// visibility, and if either is missing the adapter refuses modules rather than guess.
    /// </summary>
    private static readonly MethodInfo? AllowTopLevelAwaitScope =
        typeof(CoreScript).GetMethod("AllowTopLevelAwaitScope", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);

    private static readonly MethodInfo? ModuleGoalScope =
        typeof(CoreScript).GetMethod("ModuleGoalScope", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);

    /// <summary>Whether this engine build exposes what module analysis needs.</summary>
    internal static bool IsAvailable =>
        AllowTopLevelAwaitScope?.ReturnType == typeof(IDisposable) && ModuleGoalScope?.ReturnType == typeof(IDisposable);

    /// <summary>Enters the parse state the engine's own module compilation uses.</summary>
    internal static IDisposable EnterTopLevelAwait() =>
        (IDisposable)AllowTopLevelAwaitScope!.Invoke(null, null)!;

    private BroilerJsModuleAnalysis(List<StaticRequest> requests, string? refusal)
    {
        Requests = requests;
        Refusal = refusal;
    }

    /// <summary>The module's static requests, in source order, one entry per specifier.</summary>
    internal IReadOnlyList<StaticRequest> Requests { get; }

    /// <summary>Why the adapter cannot run this module through the contract, or null.</summary>
    internal string? Refusal { get; }

    /// <summary>One static request: a specifier and its attributes.</summary>
    internal readonly record struct StaticRequest(string Specifier, IReadOnlyList<KeyValuePair<string, string>> Attributes);

    /// <summary>Parses <paramref name="text"/> with the module goal and analyzes it.</summary>
    /// <remarks>The engine has already compiled the text by the time this runs, so a parse failure here is not expected.</remarks>
    internal static BroilerJsModuleAnalysis Analyze(string text)
    {
        AstProgram program;
        using (EnterTopLevelAwait())
        using ((IDisposable)ModuleGoalScope!.Invoke(null, null)!)
        {
            StringSpan code = text;
            program = new FastParser(new FastTokenStream(in code, null)).ParseProgram();
        }

        var requests = new List<StaticRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var statements = program.Statements.GetFastEnumerator();
        while (statements.MoveNext(out var statement))
        {
            switch (statement)
            {
                case AstImportStatement import:
                    AddRequest(import.Source.StringValue, import.Attributes);
                    break;

                case AstExportStatement { Source: AstLiteral source } export:
                    AddRequest(source.StringValue, export.Attributes);
                    break;
            }
        }

        var refusal = ImportCallFinder.Contains(program)
            ? "import() is not routed through the JSEAL module contract by the Broiler.JS adapter: the engine resolves " +
              "its specifier at run time, which the adapter cannot answer from the host synchronously (I12)"
            : null;

        return new BroilerJsModuleAnalysis(requests, refusal);

        void AddRequest(string specifier, IFastEnumerable<(StringSpan key, AstLiteral value)>? attributes)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (attributes is not null)
            {
                var attribute = attributes.GetFastEnumerator();
                while (attribute.MoveNext(out var pair))
                    pairs.Add(new(pair.key.Value ?? string.Empty, pair.value.StringValue));
            }

            if (seen.Add(specifier) || pairs.Count > 0)
                requests.Add(new StaticRequest(specifier, pairs));
        }
    }

    /// <summary>Whether any node of a tree is an <c>import()</c> call.</summary>
    /// <remarks>
    /// The walk visits every public field of every node by reflection rather than through the engine's
    /// <c>AstReduce</c>, which does not descend into variable initializers, object literal members,
    /// parameter defaults or switch cases. A call it missed would reach the engine unrouted.
    /// </remarks>
    private static class ImportCallFinder
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo[]> ChildFields = new();

        internal static bool Contains(AstNode root)
        {
            var visited = new HashSet<AstNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            return Visit(root);

            bool Visit(object? value)
            {
                switch (value)
                {
                    case null or string or StringSpan or FastToken:
                        return false;

                    case AstImportCall:
                        return true;

                    case AstNode node:
                        return visited.Add(node) && FieldsOf(node.GetType()).Any(field => Visit(field.GetValue(node)));

                    case IEnumerable items:
                        foreach (var item in items)
                        {
                            if (Visit(item))
                                return true;
                        }

                        return false;

                    default:
                        // Declarators, object properties, switch cases and tuples carry nodes in fields.
                        var type = value.GetType();
                        return type.IsValueType && !type.IsPrimitive && !type.IsEnum
                            && FieldsOf(type).Any(field => Visit(field.GetValue(value)));
                }
            }
        }

        private static FieldInfo[] FieldsOf(Type type) =>
            ChildFields.GetOrAdd(type, static type => type
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => !(field.FieldType.IsPrimitive || field.FieldType.IsEnum
                    || field.FieldType == typeof(string) || field.FieldType == typeof(StringSpan)
                    || field.FieldType == typeof(FastToken)))
                .ToArray());
    }
}
