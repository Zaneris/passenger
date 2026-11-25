using System.CodeDom;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>A statement of the surface's code as Windows Forms' serializers write it: its key (<see cref="DesignerKeys"/>),
/// the component whose section it is in (none before the first), its code in the file's style and as CodeDOM writes it.</summary>
internal sealed record SerializedLine(string? Key, string? Section, string Text, string Exact);

/// <summary>A field the surface's code declares: its name and its declaration in the file's style.</summary>
internal sealed record SerializedField(string Name, string Text);

/// <summary>What the surface holds as code, as the classic designer would write its InitializeComponent: Windows Forms' own
/// serializers (the root's type serializer, each component's, the designers' shadowed properties) asked for the class, each
/// statement keyed as Passenger's reader keys the file's, so a change between two of these is placed where the file writes it.</summary>
internal sealed record Serialized(IReadOnlyList<SerializedLine> Lines, IReadOnlyList<SerializedField> Fields)
{
    public static Serialized Of(IDesignerHost host, CodeText text)
    {
        DesignerSerializationManager manager = new(host);
        using IDisposable session = manager.CreateSession();
        if (manager.GetSerializer(host.RootComponent.GetType(), typeof(TypeCodeDomSerializer)) is not TypeCodeDomSerializer serializer)
        {
            throw new DesignerException($"Windows Forms has no serializer for {host.RootComponent.GetType().Name}");
        }
        CodeTypeDeclaration declared = serializer.Serialize(manager, host.RootComponent, host.Container.Components);
        if (manager.Errors.Count > 0)
        {
            throw new DesignerException($"Windows Forms could not write the form's code: {string.Join("; ", manager.Errors.Cast<object>().Select(e => e is Exception error ? error.Message : e.ToString()))}");
        }
        CodeMemberMethod method = declared.Members.OfType<CodeMemberMethod>().FirstOrDefault(m => m.Name == "InitializeComponent")
            ?? throw new DesignerException("Windows Forms wrote no InitializeComponent");
        HashSet<string> fields = [.. declared.Members.OfType<CodeMemberField>().Select(f => f.Name)];
        List<SerializedLine> lines = [];
        string? section = null;
        foreach (CodeStatement statement in method.Statements)
        {
            if (statement is CodeCommentStatement { Comment.Text: var comment })
            {
                // The serializer heads each component's statements with its name between two empty comments.
                if (comment.Trim() is { Length: > 0 } name)
                {
                    section = name;
                }
                continue;
            }
            if (statement is CodeAssignStatement { Left: CodePropertyReferenceExpression { PropertyName: "Enabled" or "Visible" }, Right: CodePrimitiveExpression { Value: true } })
            {
                // The control designers' shadows of these say a default is to be written; their designer never writes it.
                continue;
            }
            lines.Add(new SerializedLine(DesignerKeys.Of(Neutral(statement, fields)), section, text.Statement(statement), text.Exact(statement)));
        }
        return new Serialized(lines, [.. declared.Members.OfType<CodeMemberField>().Select(f => new SerializedField(f.Name, text.Field(f)))]);
    }

    /// <summary>A statement as Passenger's reader reads the same code, as far as its key needs: what it does to which
    /// component's which member, a call's components, a hook's method.</summary>
    private static DesignerStatement Neutral(CodeStatement statement, HashSet<string> fields)
    {
        switch (statement)
        {
            case CodeAssignStatement { Left: CodeFieldReferenceExpression { TargetObject: CodeThisReferenceExpression, FieldName: var field } } when fields.Contains(field):
                return new DesignerStatement(DesignerAct.Create, field, null, 0);
            case CodeAssignStatement { Left: var left } when Path(left) is { } path:
                return Of(DesignerAct.Assign, path, fields);
            case CodeAttachEventStatement { Event: var hooked, Listener: var listener } when Path(hooked.TargetObject) is { } on:
                return Of(DesignerAct.Hook, [.. on, hooked.EventName], fields) with
                {
                    Value = new DesignerValue(DesignerValueKinds.Name) { Name = listener is CodeDelegateCreateExpression { MethodName: var handler } ? handler : null },
                };
            case CodeExpressionStatement { Expression: CodeMethodInvokeExpression { Method: var method, Parameters: var parameters } }
                when (method.TargetObject is CodeCastExpression { Expression: var cast } ? Path(cast) : Path(method.TargetObject)) is { } target:
                return Of(DesignerAct.Call, [.. target, method.MethodName], fields) with
                {
                    Arguments = [.. parameters.Cast<CodeExpression>().Select(p => Path(p) is [.., var last] ? new DesignerValue(DesignerValueKinds.Name) { Name = last }
                        : new DesignerValue(DesignerValueKinds.Unread))],
                };
            case CodeVariableDeclarationStatement local:
                return new DesignerStatement(DesignerAct.Other, local.Name, null, 0);
        }
        return new DesignerStatement(DesignerAct.Other, string.Empty, null, 0);
    }

    private static DesignerStatement Of(DesignerAct act, IReadOnlyList<string> path, HashSet<string> fields) =>
        fields.Contains(path[0]) && path.Count > 1
            ? new DesignerStatement(act, path[0], string.Join('.', path.Skip(1)), 0)
            : new DesignerStatement(act, string.Empty, string.Join('.', path), 0);

    /// <summary>The names an expression reads, outermost first, the class itself left out; null for one that is no chain of
    /// names (a call's result, a new object).</summary>
    private static List<string>? Path(CodeExpression? expression) => expression switch
    {
        null or CodeThisReferenceExpression => [],
        CodeFieldReferenceExpression field when Path(field.TargetObject) is { } on => [.. on, field.FieldName],
        CodePropertyReferenceExpression property when Path(property.TargetObject) is { } on => [.. on, property.PropertyName],
        CodeVariableReferenceExpression variable => [variable.VariableName],
        _ => null,
    };
}
