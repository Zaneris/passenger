using Passenger.App.Editors;

namespace Passenger.UnitTests.Editors;

/// <summary>The question before Undo or Redo of a command of several files names all it does to them: the texts it changes,
/// each file it renames or moves back, and those it brings back or deletes, by their names as they stand now.</summary>
public sealed class UndoQuestionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shop");

    private static string At(params string[] names) => Path.Combine([Root, .. names]);

    [Fact]
    public void Undo_of_a_files_rename_with_its_type_says_the_file_is_renamed_back_as_well_as_changed()
    {
        string question = UndoQuestion.Of("Rename Widget to Gadget", redo: false, [At("Uses.cs"), At("Gadget.cs")], [(At("Gadget.cs"), At("Widget.cs"))], [], [], []);
        Assert.Equal("Undo Rename Widget to Gadget? It changes Uses.cs and Gadget.cs, and renames Gadget.cs back to Widget.cs.", question);
    }

    [Fact]
    public void Redo_of_a_rename_says_the_file_is_renamed_again_not_back()
    {
        string question = UndoQuestion.Of("Rename Widget to Gadget", redo: true, [At("Uses.cs"), At("Widget.cs")], [(At("Widget.cs"), At("Gadget.cs"))], [], [], []);
        Assert.Equal("Redo Rename Widget to Gadget? It changes Uses.cs and Widget.cs, and renames Widget.cs to Gadget.cs.", question);
    }

    [Fact]
    public void A_rename_alone_says_only_the_rename()
    {
        string question = UndoQuestion.Of("Rename Shapes.cs to Figures.cs", redo: false, [], [(At("Figures.cs"), At("Shapes.cs"))], [], [], []);
        Assert.Equal("Undo Rename Shapes.cs to Figures.cs? It renames Figures.cs back to Shapes.cs.", question);
    }

    [Fact]
    public void A_move_to_another_folder_names_the_folder_it_goes_back_to()
    {
        string question = UndoQuestion.Of("Move Shop.cs to Model", redo: false, [], [(At("Model", "Shop.cs"), At("Src", "Shop.cs"))], [], [], []);
        Assert.Equal("Undo Move Shop.cs to Model? It moves Shop.cs back to Src/Shop.cs.", question);
    }

    [Fact]
    public void A_file_the_command_made_is_said_deleted_by_undo_and_brought_back_by_redo_never_changed()
    {
        string undo = UndoQuestion.Of("Move to Another File", redo: false, [At("Shapes.cs"), At("Circle.cs")], [], [], [At("Circle.cs")], []);
        Assert.Equal("Undo Move to Another File? It changes Shapes.cs, and deletes Circle.cs.", undo);
        string redo = UndoQuestion.Of("Move to Another File", redo: true, [At("Shapes.cs"), At("Circle.cs")], [], [At("Circle.cs")], [], []);
        Assert.Equal("Redo Move to Another File? It changes Shapes.cs, and brings back Circle.cs.", redo);
    }

    [Fact]
    public void Every_kind_of_change_is_said_in_turn_with_what_else_the_command_changes()
    {
        string question = UndoQuestion.Of("Rename Old.cs to New.cs", redo: false, [At("Uses.cs")], [(At("New.cs"), At("Old.cs"))], [At("Taken.cs")], [At("Made.cs")],
            ["run configurations"]);
        Assert.Equal("Undo Rename Old.cs to New.cs? It changes Uses.cs and run configurations, renames New.cs back to Old.cs, brings back Taken.cs, and deletes Made.cs.", question);
    }
}
