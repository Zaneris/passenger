using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The language server's help through the whole window, on a real project: completion as you type, go to
/// definition, rename. Each test opens its own window, on the server the last one left running, warm, and on the
/// project as written (<see cref="SharedProject"/>); what it counts (the UI thread's allocations, the server's
/// requests) a busy machine leaves as it is.</summary>
public sealed class CodeAssistTests : IClassFixture<CodeAssistTests.Project>
{
    private readonly Project project;

    public CodeAssistTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>The gate's completion, edits and cards of C#, as one flow.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_gates_completion_edits_and_cards_hold_as_one_flow()
    {
        Flow.StepApart("a member chosen from the list a dot opened with nothing typed leaves the caret after it", () => new CodeAssistTests(project), t => t.A_member_chosen_from_the_list_a_dot_opened_with_nothing_typed_leaves_the_caret_after_it());
        Flow.StepApart("choosing await in a method not yet async makes the method async as well", () => new CodeAssistTests(project), t => t.Choosing_await_in_a_method_not_yet_async_makes_the_method_async_as_well());
        Flow.StepApart("Undo and Redo of a paste at a file's end keep their steps and the server's copy, with an input method following", () => new CodeAssistTests(project), t => t.Undo_and_redo_of_a_paste_at_a_files_end_keep_their_steps_and_the_servers_copy_with_an_input_method_following());
        Flow.StepApart("Code Cleanup removes unused usings, sorts the rest and reformats", () => new CodeAssistTests(project), t => t.Code_cleanup_removes_unused_usings_sorts_the_rest_and_reformats());
        Flow.StepApart("parameter info keeps a sentence apart from a parameter its documentation names straight after it", () => new CodeAssistTests(project), t => t.Parameter_info_keeps_a_sentence_apart_from_a_parameter_its_documentation_names_straight_after_it());
        Flow.StepApart("the error stripe marks a problem in its colour, says what it is, and a click goes there", () => new CodeAssistTests(project), t => t.The_error_stripe_marks_a_problem_in_its_colour_says_what_it_is_and_a_click_goes_there());
    }

    /// <summary>The gate's navigation and refactorings of C#, as one flow.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_gates_navigation_and_refactorings_hold_as_one_flow()
    {
        Flow.StepApart("Go to Declaration, Back and Forward land where the code is, and a library type opens its decompiled source read-only", () => new CodeAssistTests(project), t => t.Go_to_declaration_back_and_forward_land_where_the_code_is_and_a_library_type_opens_its_decompiled_source_read_only());
        Flow.StepApart("a right click on a call puts the caret there, and its menu renames it in every file that uses it and goes to an implementation", () => new CodeAssistTests(project), t => t.A_right_click_on_a_call_puts_the_caret_there_and_its_menu_renames_it_in_every_file_that_uses_it_and_goes_to_an_implementation());
        Flow.StepApart("call and type hierarchies show who calls a method and what derives from a type", () => new CodeAssistTests(project), t => t.Call_and_type_hierarchies_show_who_calls_a_method_and_what_derives_from_a_type());
        Flow.StepApart("Extract Method asks each new method's name and keeps the next statement on its line, and Refactor This lists only what fits", () => new CodeAssistTests(project), t => t.Extract_method_asks_each_new_methods_name_and_keeps_the_next_statement_on_its_line_and_refactor_this_lists_only_what_fits());
        Flow.StepApart("a fix with choices offers them as a second list and makes the one chosen", () => new CodeAssistTests(project), t => t.A_fix_with_choices_offers_them_as_a_second_list_and_makes_the_one_chosen());
        Flow.StepApart("Fix All offers its scopes and fixes every occurrence in the one chosen", () => new CodeAssistTests(project), t => t.Fix_all_offers_its_scopes_and_fixes_every_occurrence_in_the_one_chosen());
    }

    /// <summary>Completion writes the item chosen as C# needs it: a named argument's colon or an attribute's equals
    /// sign, one separator after a declaration keyword, an extension method's using, a method called with the caret in
    /// its brackets in the name's step of Undo or alone with brackets turned off, the item before a commit character
    /// with its documentation beside the list, CamelHumps' names, and the members to override after a space; the names
    /// of the type the place takes listed first, and a list still asked for let go by an Escape.</summary>
    [AvaloniaFact]
    public void Completion_writes_the_item_chosen_with_its_separator_brackets_using_and_step_of_undo()
    {
        Flow.StepApart("a named argument chosen by Enter writes its colon", () => new CodeAssistTests(project), t => t.Choosing_a_named_argument_writes_its_colon("ENTER", false));
        Flow.StepApart("an attribute's named argument chosen by Tab before an equals sign keeps one", () => new CodeAssistTests(project), t => t.Choosing_an_attributes_named_argument_keeps_its_equals_sign("TAB", true));
        Flow.StepApart("a declaration keyword chosen leaves the caret after one separator", () => new CodeAssistTests(project), t => t.Declaration_completion_leaves_the_caret_after_one_separator("ENTER", " "));
        Flow.StepApart("completion lists first the names of the type the place takes, keeping the rest", () => new CodeAssistTests(project), t => t.Completion_lists_first_the_names_of_the_type_the_place_takes_keeping_the_rest());
        Flow.StepApart("an extension method chosen after a dot adds its using above and leaves the caret after the method", () => new CodeAssistTests(project), t => t.An_extension_method_chosen_after_a_dot_adds_its_using_above_and_leaves_the_caret_after_the_method());
        Flow.StepApart("a method chosen from the list is written called, the caret inside its brackets when it takes arguments", () => new CodeAssistTests(project), t => t.A_method_chosen_from_the_list_is_written_called_the_caret_inside_its_brackets_when_it_takes_arguments());
        Flow.StepApart("a method chosen is written at once, its brackets following in its step of Undo unless typed on before they came", () => new CodeAssistTests(project), t => t.A_method_chosen_is_written_at_once_its_brackets_following_in_its_step_of_undo_unless_typed_on_before_they_came());
        Flow.StepApart("with parentheses turned off a method chosen from the list is written as its name alone", () => new CodeAssistTests(project), t => t.With_parentheses_turned_off_a_method_chosen_from_the_list_is_written_as_its_name_alone());
        Flow.StepApart("typing after a dot completes, filtered as you type, and Enter commits it", () => new CodeAssistTests(project), t => t.Typing_after_a_dot_completes_filtered_as_you_type_and_enter_commits_it_without_the_visual_studio_assemblies_passenger_leaves_out());
        Flow.StepApart("CamelHumps typed offer the names they start after the prefix matches, and Enter writes the one chosen", () => new CodeAssistTests(project), t => t.CamelHumps_typed_offer_the_names_they_start_after_the_prefix_matches_and_enter_writes_the_one_chosen());
        Flow.StepApart("a space after override lists the members to override and no template, and Enter writes one", () => new CodeAssistTests(project), t => t.A_space_after_override_lists_the_members_to_override_and_no_template_and_Enter_writes_one());
        Flow.StepApart("a list still asked for is let go by an Escape or the caret moved out of its name", () => new CodeAssistTests(project), t => t.A_list_still_asked_for_is_let_go_by_an_escape_or_the_caret_moved_out_of_its_name());
        Flow.StepApart("a commit character types the chosen item before it, and the item's documentation shows beside the list", () => new CodeAssistTests(project), t => t.A_commit_character_types_the_chosen_item_before_it_and_the_items_documentation_shows_beside_the_list());
    }

    /// <summary>A completion list as the window keeps it: its first rows laid out as the window opened, narrowed off
    /// the window's thread laying out only the rows that came or went, a statement's in-scope rows before the types of
    /// namespaces not imported which join them, its whole list narrowed here asking the server nothing, and one of more
    /// than 10,000 in the server's order.</summary>
    [AvaloniaFact]
    public void A_completion_list_is_laid_out_ahead_narrowed_off_the_window_and_comes_in_its_parts_in_the_servers_order()
    {
        Flow.StepApart("a window's first completion list shows in the rows laid out as the window opened", () => new CodeAssistTests(project), t => t.A_windows_first_completion_list_shows_in_the_rows_laid_out_as_the_window_opened());
        Flow.StepApart("typing into a completion list narrows it off the window's thread, in the rows it shows", () => new CodeAssistTests(project), t => t.Typing_into_a_completion_list_narrows_it_off_the_UI_thread_in_the_rows_it_shows());
        Flow.StepApart("a key that changes how many rows the completion list shows lays out only the rows that came or went", () => new CodeAssistTests(project), t => t.A_key_that_changes_how_many_rows_the_completion_list_shows_lays_out_only_the_rows_that_came_or_went());
        Flow.StepApart("a statement's first rows show before the types of namespaces not imported, which join them leaving the choice", () => new CodeAssistTests(project), t => t.A_statements_first_rows_show_before_the_types_of_namespaces_not_imported_which_join_them_leaving_the_choice());
        Flow.StepApart("a statement's whole list narrows here as its name is typed, asking the server nothing", () => new CodeAssistTests(project), t => t.A_statements_whole_list_narrows_here_as_its_name_is_typed_asking_the_server_nothing());
        Flow.StepApart("a statement's list of more than ten thousand lists the names found alike in the server's order", () => new CodeAssistTests(project), t => t.A_statements_list_of_more_than_ten_thousand_lists_the_names_found_alike_in_the_servers_order());
    }

    /// <summary>What the window says of the code around the caret: the status bar's path, a hover card and File
    /// Structure, once the server has the file; the structure window following the editor; Go to Symbol's generated
    /// declarations; and parameter info's overloads, those the arguments no longer fit set back.</summary>
    [AvaloniaFact]
    public void The_code_around_the_caret_is_named_and_its_overloads_shown_as_the_server_has_it()
    {
        Flow.StepApart("the path, a hover card, File Structure and Go to Symbol name the code and go to it", () => new CodeAssistTests(project), t => t.The_path_a_hover_card_file_structure_and_go_to_symbol_name_the_code_and_go_to_it());
        Flow.StepApart("a file shown before its folder's server has it names the member the caret is in once it does", () => new CodeAssistTests(project), t => t.A_file_shown_before_its_folders_server_has_it_names_the_member_the_caret_is_in_once_it_does());
        Flow.StepApart("the structure window shows the shown file's outline whenever it is shown, once the server has the file, and follows the editor", () => new CodeAssistTests(project), t => t.The_structure_window_shows_the_shown_files_outline_whenever_it_is_shown_and_once_the_server_has_the_file_and_follows_the_editor());
        Flow.StepApart("Go to Symbol lists a generated method's own declaration alone, and what only a generator wrote after", () => new CodeAssistTests(project), t => t.Go_to_Symbol_lists_a_generated_methods_own_declaration_alone_and_what_only_a_generator_wrote_after());
        Flow.StepApart("parameter info shows every overload, setting back those the arguments no longer fit", () => new CodeAssistTests(project), t => t.Parameter_info_shows_every_overload_setting_back_those_the_arguments_no_longer_fit());
    }

    /// <summary>Find Usages groups, previews and keeps each search, lists each place once and gives its results the
    /// keys; Show Usages hands them to the Find window; a file another program has just written lists its caller; and
    /// the call hierarchy finds callers at a use in another file and a record's positional property.</summary>
    [AvaloniaFact]
    public void Usages_and_callers_are_listed_once_each_given_the_keys_and_followed_into_a_file_just_written()
    {
        Flow.StepApart("Find Usages groups them, previews the chosen one, keeps each search and lists each place once at its line", () => new CodeAssistTests(project), t => t.Find_usages_groups_them_previews_the_chosen_one_keeps_each_search_and_lists_each_place_once_at_its_line());
        Flow.StepApart("Show Usages lists them in a popup under the caret, goes to the one chosen and hands them to the Find window", () => new CodeAssistTests(project), t => t.Show_usages_lists_them_in_a_popup_under_the_caret_goes_to_the_one_chosen_and_hands_them_to_the_find_window());
        Flow.StepApart("Find Usages gives its results the keys, so Down and Enter open the next usage", () => new CodeAssistTests(project), t => t.Find_usages_gives_its_results_the_keys_so_Down_and_Enter_open_the_next_usage());
        Flow.StepApart("Find Usages in a file another program has just written lists the caller it wrote beside it", () => new CodeAssistTests(project), t => t.Find_usages_in_a_file_another_program_has_just_written_lists_the_caller_it_wrote_beside_it());
        Flow.StepApart("the call hierarchy at a use in another file and at a record's positional property shows its callers", () => new CodeAssistTests(project), t => t.Call_hierarchy_at_a_use_in_another_file_and_at_a_records_positional_property_shows_its_callers());
    }

    /// <summary>Reformat Code of a method, on save as one step of Undo or turned off, changes only its own file and
    /// never a letter typed meanwhile; and what a reformat, a completion, a template or a fix writes takes the file's
    /// line breaks and indents as its .editorconfig says, else as the file's own lines; an .editorconfig is read as
    /// Roslyn's server and the projects' analyzers read it.</summary>
    [AvaloniaFact]
    public void Reformat_and_what_a_completion_or_fix_writes_keep_the_files_indent_and_line_breaks_and_only_their_own_file()
    {
        Flow.StepApart("a reformat started before switching tabs changes only its own file and breaks lines as the file does", () => new CodeAssistTests(project), t => t.A_reformat_started_before_switching_tabs_changes_only_its_own_file_and_breaks_lines_as_the_file_does());
        Flow.StepApart("Reformat Code with a method selected reformats that method alone and leaves the code around it", () => new CodeAssistTests(project), t => t.Reformat_code_with_a_method_selected_reformats_that_method_alone_and_leaves_the_code_around_it());
        Flow.StepApart("Reformat on Save lays a file out before writing it as one step of Undo, and without the server or turned off saves it as typed", () => new CodeAssistTests(project), t => t.Reformat_on_save_lays_a_file_out_before_writing_it_as_one_step_of_undo_and_without_the_server_or_turned_off_saves_it_as_typed());
        Flow.StepApart("Reformat Code over a folder and Code Cleanup over the project from the Explorer each change every file as one step of Undo", () => new CodeAssistTests(project), t => t.Reformat_code_over_a_folder_and_code_cleanup_over_the_project_from_the_explorer_each_change_every_file_as_one_step_of_undo());
        Flow.StepApart("a letter typed at the start of what a reformat is about to change stays once the reformat lands", () => new CodeAssistTests(project), t => t.A_letter_typed_at_the_start_of_what_a_reformat_is_about_to_change_stays_once_the_reformat_lands());
        Flow.StepApart("what a completion or a fix writes takes the file's line breaks, whichever the server writes", () => new CodeAssistTests(project), t => t.What_a_completion_or_a_fix_writes_takes_the_files_line_breaks_whichever_the_server_writes());
        Flow.StepApart("a tab-indented .editorconfig has Enter, Tab, a template, a completion and a fix indent with tabs", () => new CodeAssistTests(project), t => t.A_tab_indented_editorconfig_has_enter_tab_a_template_a_completion_and_a_fix_indent_with_tabs());
        Flow.StepApart("where no .editorconfig says, completions and reformatting indent as the file's own lines", () => new CodeAssistTests(project), t => t.Where_no_editorconfig_says_completions_and_reformatting_indent_as_the_files_own_lines());
        Flow.StepApart("an .editorconfig marks what nothing reads, says what a key does and completes Roslyn's options and their values", () => new CodeAssistTests(project), t => t.An_editorconfig_marks_what_nothing_reads_says_what_a_key_does_and_completes_roslyns_options_and_their_values());
    }

    /// <summary>Edits keep the language server in step below line breaks only Roslyn counts, between a CRLF's halves
    /// and past what Passenger reads: code typed there squiggled, coloured and reformatted in place, Go to Symbol,
    /// Declaration, Implementation and a refactoring's lines landing on the editor's, and a paste too deep said and
    /// kept from the server until undone.</summary>
    [AvaloniaFact]
    public void Edits_keep_the_server_in_step_below_line_breaks_only_roslyn_counts_and_past_what_passenger_reads()
    {
        Flow.StepApart("code typed below a comment holding line breaks only Roslyn counts is squiggled, coloured and reformatted in place", () => new CodeAssistTests(project), t => t.Code_typed_below_a_comment_holding_line_breaks_only_roslyn_counts_is_squiggled_coloured_and_reformatted_in_place());
        Flow.StepApart("edits that make and unmake line breaks only Roslyn counts, or fall between a CRLF's halves, keep the server in step", () => new CodeAssistTests(project), t => t.Edits_that_make_and_unmake_line_breaks_only_roslyn_counts_or_fall_between_a_crlfs_halves_keep_the_server_in_step());
        Flow.StepApart("Go to Symbol, Declaration, Implementation and a refactoring's changed lines are the editor's below line breaks only Roslyn counts", () => new CodeAssistTests(project), t => t.Go_to_symbol_declaration_implementation_and_a_refactorings_changed_lines_are_the_editors_below_line_breaks_only_roslyn_counts());
        Flow.StepApart("a paste past what Passenger reads is said and kept from the server until it is undone", () => new CodeAssistTests(project), t => t.A_paste_past_what_passenger_reads_is_said_and_kept_from_the_server_until_it_is_undone());
    }

    /// <summary>A library type's decompiled source refuses every change and stays as another window's server decompiles
    /// one; a rename keeps a line typed while it is out and a file's unsaved edits; and an agent opening the open
    /// folder again keeps its language server.</summary>
    [AvaloniaFact]
    public void Decompiled_code_refuses_changes_and_renames_keep_the_persons_typing_and_unsaved_edits()
    {
        Flow.StepApart("every action of the Code and Refactor menus that would change a decompiled file greys, saying it is read-only", () => new CodeAssistTests(project), t => t.Every_action_of_the_code_and_refactor_menus_that_would_change_a_decompiled_file_greys_saying_it_is_read_only());
        Flow.StepApart("a library type's source decompiled for one window stays as another window's server decompiles one", () => new CodeAssistTests(project), t => t.A_library_types_source_decompiled_for_one_window_stays_as_another_windows_server_decompiles_one());
        Flow.StepApart("a line typed while a rename is out still leaves the new name on the symbol", () => new CodeAssistTests(project), t => t.A_line_typed_while_a_rename_is_out_still_leaves_the_new_name_on_the_symbol());
        Flow.StepApart("renaming a file to match its type with unsaved edits leaves one file of the new name holding them", () => new CodeAssistTests(project), t => t.Renaming_a_file_to_match_its_type_with_unsaved_edits_leaves_one_file_of_the_new_name_holding_them());
        Flow.StepApart("opening the open folder again through MCP keeps the open file's language server", () => new CodeAssistTests(project), t => t.Opening_the_open_folder_again_through_mcp_keeps_the_open_files_language_server());
    }

    /// <summary>Fixes and context actions do what they offer: Fix All in the solution undone in every file at once, a
    /// fix the server will not make saying why, an agent's action with choices; Escape closes their lists wherever the
    /// keys are; live and postfix templates expand and end as their editor moves on; Extend Selection grows and
    /// shrinks; and type and parameter hints show and go with Settings.</summary>
    [AvaloniaFact]
    public void Fixes_context_actions_templates_and_selection_do_what_they_offer_and_take_back_whole()
    {
        Flow.StepApart("Fix All in the solution reaching a file not open is undone in every file it changed at once", () => new CodeAssistTests(project), t => t.Fix_all_in_the_solution_reaching_a_file_not_open_is_undone_in_every_file_it_changed_at_once());
        Flow.StepApart("a fix the language server will not make, once chosen, says why in the status bar", () => new CodeAssistTests(project), t => t.A_fix_the_language_server_will_not_make_once_chosen_says_why_in_the_status_bar());
        Flow.StepApart("through MCP an action with choices lists them, asks for one and applies the one named", () => new CodeAssistTests(project), t => t.Through_mcp_an_action_with_choices_lists_them_asks_for_one_and_applies_the_one_named());
        Flow.StepApart("Escape closes the lists of choices and the rename box wherever the keys are", () => new CodeAssistTests(project), t => t.Escape_closes_the_lists_of_choices_and_the_rename_box_wherever_the_keys_are());
        Flow.StepApart("live and postfix templates expand where they belong, fill their variables and wrap what they follow", () => new CodeAssistTests(project), t => t.Live_and_postfix_templates_expand_where_they_belong_fill_their_variables_and_wrap_what_they_follow());
        Flow.StepApart("a live template ends when its editor shows another file, and Enter there breaks the line", () => new CodeAssistTests(project), t => t.A_live_template_ends_when_its_editor_shows_another_file_and_enter_there_breaks_the_line());
        Flow.StepApart("Extend Selection grows through the code around the caret, and Shrink takes it back", () => new CodeAssistTests(project), t => t.Extend_selection_grows_through_the_code_around_the_caret_and_shrink_takes_it_back());
        Flow.StepApart("type and parameter name hints show, make room, step as one place and go with Settings", () => new CodeAssistTests(project), t => t.Type_and_parameter_name_hints_show_where_Rider_shows_them_make_room_step_as_one_place_and_go_with_Settings());
        Flow.StepApart("with both kinds of hint off no hints are asked for, and turning one on brings its hints at once", () => new CodeAssistTests(project), t => t.With_both_kinds_of_hint_off_no_hints_are_asked_for_and_turning_one_on_brings_its_hints_at_once());
    }

    /// <summary>What the window says of the code around the caret, in one window on Program.cs: the status bar's path names
    /// the file and the member the caret is in; resting the pointer on a method shows a card with its signature; File
    /// Structure lists the file's members, opens on the caret's and filters keeping their type; and Go to Symbol finds a
    /// member in another file by name and goes to it.</summary>
    private void The_path_a_hover_card_file_structure_and_go_to_symbol_name_the_code_and_go_to_it()
    {
        MainWindow window = WithoutNavigationBar(project.Open("Program.cs"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.WaitFor(() => window.Breadcrumbs.Count == 3, "the member the caret is in");
        Assert.Equal(["Program.cs", "Program", "Main()"], window.Breadcrumbs);
        editor.GoTo(new TextPosition(1, 0));
        Ui.Settle();
        Assert.Equal(["Program.cs", "Program"], window.Breadcrumbs);

        int at = editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + 2;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hover card");
        Assert.Contains("int Helper.Twice(int value)", window.Assist.HoverText, StringComparison.Ordinal);
        window.MouseMove(new Point(1, 1), Avalonia.Input.RawInputModifiers.None);
        Ui.Settle();

        editor.GoTo(new TextPosition(4, 8));
        Ui.Run(window, AppActions.FileStructure);
        SymbolPopup structure = Assert.IsType<SymbolPopup>(window.Popup);
        Ui.WaitFor(() => structure.IsSettled && structure.Rows.Count > 0, "the file's outline");
        Assert.Equal([("Program", 0), ("Main()", 1)], structure.Rows.Select(r => (r.Name, r.Depth)));
        Assert.Equal(": void", structure.Rows[1].Detail);
        Assert.Equal("Main()", structure.Selected?.Name);
        window.KeyTextInput("Ma");
        Ui.WaitFor(() => structure.IsSettled && structure.Rows.Count == 2, "the filtered outline");
        Ui.Press(window, "Enter");
        Assert.Equal(2, editor.CaretPosition.Line);

        Ui.Run(window, AppActions.GoToSymbol);
        SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
        window.KeyTextInput("Twic");
        Ui.WaitFor(() => popup.Rows.Any(r => r.Name.StartsWith("Twice", StringComparison.Ordinal)), "the server's symbols for what was typed");
        SymbolRow twice = popup.Rows.First(r => r.Name.StartsWith("Twice", StringComparison.Ordinal));
        Assert.Equal("Helper", twice.Detail);
        Assert.Equal("Helper.cs:3", twice.Where);

        Ui.Press(window, "Enter");
        Assert.Null(window.Popup);
        Assert.Equal(PathNames.Canonical(project.PathOf("Helper.cs")), window.EditorArea.Active?.Path);
        Assert.Equal(2, window.EditorArea.CodeEditor.CaretPosition.Line);
        window.Close();
    }

    /// <summary>Where the status bar's path is drawn.</summary>
    private static Control PathHost(MainWindow window) => window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "BreadcrumbHost");

    /// <summary>A window with the navigation bar turned off, so the status bar's path names the type and member the caret is
    /// in, which these tests read there.</summary>
    private static MainWindow WithoutNavigationBar(MainWindow window)
    {
        window.ApplySettings(window.Settings with { ShowNavigationBar = false });
        Ui.Settle();
        return window;
    }

    /// <summary>What marks a part of the path its edge cuts, as <see cref="DrawnPath"/> reads it.</summary>
    private const string CutMark = " (cut by the path's edge)";

    /// <summary>The status bar's path as it draws it, part by part: each part's words as laid out, those the path's edge cuts
    /// marked so, those it lays outside its room left out.</summary>
    private static string[] DrawnPath(MainWindow window)
    {
        Control host = PathHost(window);
        return [.. host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.FindAncestorOfType<Button>() is not null)
            .Select(t => (Text: Ui.Drawn(t), At: t.TranslatePoint(default, host) ?? default, t.Bounds.Width))
            .Where(t => t.At.X + t.Width > 0.5 && t.At.X < host.Bounds.Width - 0.5)
            .Select(t => t.At.X < -0.5 || t.At.X + t.Width > host.Bounds.Width + 0.5 ? t.Text + CutMark : t.Text)];
    }

    /// <summary>A file shown before its folder's language server has it names the member the caret is in once the server
    /// does, though nothing changes after: here a file opened alone, its folder opened after, on a server already loaded
    /// (the one the class's last window left), which is ready before the file is opened in it.</summary>
    private void A_file_shown_before_its_folders_server_has_it_names_the_member_the_caret_is_in_once_it_does()
    {
        MainWindow first = project.Open(file: null);
        int warm = first.CSharpService!.Session!.Server.ProcessId;
        first.Close();
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, WarmServer = project.Server, StateFolder = TestFolders.Create("assist-state-") };
        window.Show();
        WithoutNavigationBar(window);
        window.OpenFile(project.PathOf("Program.cs"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.Settle();
        Assert.Equal(["Program.cs"], window.Breadcrumbs);

        window.OpenFolder(project.Folder);
        Ui.WaitFor(() => window.CSharpService is { IsReady: true } && window.CSharpService.For(window.EditorArea.Active!)?.Lsp is not null,
            "the language server to have the file");
        Assert.Equal(warm, window.CSharpService!.Session!.Server.ProcessId);
        Ui.Settle();
        Assert.True(window.Breadcrumbs.SequenceEqual(["Program.cs", "Program", "Main()"]),
            $"Once the language server had the file, the status bar's path named [{string.Join(" › ", window.Breadcrumbs)}], not the member the caret is in");
        window.Close();
    }

    /// <summary>Go to Symbol lists the solution's own declarations first, never a source generator's part of one of them: a
    /// [GeneratedRegex] method is found in its own file alone, and what only the generator declares comes after, said to be
    /// generated, nothing at a path no file is at.</summary>
    private void Go_to_Symbol_lists_a_generated_methods_own_declaration_alone_and_what_only_a_generator_wrote_after()
    {
        project.Write("Words.cs", "using System.Text.RegularExpressions;\n\nstatic partial class Words\n{\n"
            + "    [GeneratedRegex(@\"\\w+\")]\n    public static partial Regex Word();\n}\n");
        MainWindow window = project.Open("Words.cs");
        try
        {
            Ui.Run(window, AppActions.GoToSymbol);
            SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
            window.KeyTextInput("Word");
            Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Where?.StartsWith("Words.cs:", StringComparison.Ordinal) == true),
                () => $"Word's own declaration among the server's symbols: [{string.Join(", ", popup.Rows.Select(r => $"{r.Name} {r.Where}"))}]");
            SymbolRow[] rows = [.. popup.Rows];
            string Listed() => string.Join(", ", rows.Select(r => $"{r.Name} {r.Where}"));
            Assert.True(rows.Count(r => r.Name.StartsWith("Word(", StringComparison.Ordinal) || r.Name == "Word") == 1
                && rows.SkipWhile(r => r.Generated is null).All(r => r.Generated is not null && r.Where?.Contains(".g.cs (generated):", StringComparison.Ordinal) == true),
                $"Go to Symbol listed the method more than once, or a generator's symbol before the person's or unsaid: {Listed()}");
            Assert.DoesNotContain(rows, r => r.Where is { } where && where.StartsWith('/'));
        }
        finally
        {
            window.Close();
        }
    }

    private void The_structure_window_shows_the_shown_files_outline_whenever_it_is_shown_and_once_the_server_has_the_file_and_follows_the_editor()
    {
        // A server started after the file opened, as a folder reopened with its files starts one.
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        ClickStripe(window, ToolWindowIds.Structure);
        window.OpenFolder(project.Folder);
        window.OpenFile("Program.cs");
        Ui.WaitFor(() => window.CSharpService?.IsReady == true, "the language server to load the project", TimeSpan.FromMinutes(2));
        Ui.Settle();
        Assert.True(window.StructureView.Rows.Select(r => r.Name).SequenceEqual(["Program", "Main()"]),
            $"Structure, open as the folder opened, still showed [{string.Join(", ", window.StructureView.Rows.Select(r => r.Name))}] once the server had the file, not its members");
        window.Close();

        // Opened from its stripe once the server has the file: the class's warm server is as good as any for that.
        window = project.Open("Program.cs");
        window.EditorArea.CodeEditor.GoTo(new TextPosition(4, 8));
        ClickStripe(window, ToolWindowIds.Structure);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Structure));
        Passenger.App.ToolWindows.StructureView structure = window.StructureView;
        Assert.True(structure.Rows.Select(r => r.Name).SequenceEqual(["Program", "Main()"]),
            $"Structure opened from its stripe showed [{string.Join(", ", structure.Rows.Select(r => r.Name))}], not the file's members");
        Assert.Equal("Main()", structure.Selected?.Name);

        window.OpenFile("Helper.cs");
        Ui.WaitFor(() => structure.Rows.FirstOrDefault()?.Name == "Helper", "the other file's outline");
        Assert.Contains(structure.Rows, r => r.Name == "Twice(int)");

        ClickStripe(window, ToolWindowIds.Structure);
        window.OpenFile("Program.cs");
        ClickStripe(window, ToolWindowIds.Structure);
        Assert.True(structure.Rows.FirstOrDefault()?.Name == "Program",
            $"Structure opened again showed [{string.Join(", ", structure.Rows.Select(r => r.Name))}], not Program.cs's members");
        window.Close();
    }

    /// <summary>Clicks a tool window's stripe button, as a person opens or closes it.</summary>
    private static void ClickStripe(MainWindow window, string id)
    {
        Avalonia.Controls.Primitives.ToggleButton button = window.DockHost.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => Equals(b.Tag, id));
        Ui.Click(button);
    }

    /// <summary>A named argument chosen by either key writes its colon where none follows the caret (the first) or takes the
    /// one there (the second), so the caret stands after one colon.</summary>
    private void Choosing_a_named_argument_writes_its_colon(string key, bool existingColon)
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Twice(");
        editor.Focus();
        if (existingColon)
        {
            window.KeyTextInput(":");
            Ui.Press(window, "LEFT");
        }
        else
        {
            Ui.Press(window, "DELETE");
        }
        Ui.Run(window, AppActions.Complete);
        window.KeyTextInput("val");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "val" && window.Assist.CompletionLabels.Contains("value:"), "the value: named argument in the narrowed list");
        foreach (string _ in window.Assist.CompletionLabels.TakeWhile(label => label != "value:"))
        {
            Ui.Press(window, "DOWN");
        }
        Assert.Equal("value:", window.Assist.CompletionChosen);
        Ui.Press(window, key);
        Ui.Settle();
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Assert.Equal(existingColon ? "        int result = Helper.Twice(value:2);" : "        int result = Helper.Twice(value:);", line);
        Assert.Equal(line.IndexOf(':', StringComparison.Ordinal) + 1, editor.CaretPosition.Column);
        window.Close();
    }

    /// <summary>An attribute's named argument chosen by either key keeps its " =": written after the name where none follows
    /// the caret, the caret after it (the first); where one follows, the list offers the name alone, which goes in before
    /// it (the second), so one " =" stands after the name either way.</summary>
    private void Choosing_an_attributes_named_argument_keeps_its_equals_sign(string key, bool existingEquals)
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "class Program\n{");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(existingEquals ? "[System.Obsolete( = \"Old\")]" : "[System.Obsolete()]");
        Ui.PlaceAfter(editor, "Obsolete(");
        editor.Focus();
        Ui.Run(window, AppActions.Complete);
        window.KeyTextInput("Diag");
        string named = existingEquals ? "DiagnosticId" : "DiagnosticId =";
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "Diag" && window.Assist.CompletionLabels.Contains(named),
            () => $"the {named} named argument in the narrowed list: {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        foreach (string _ in window.Assist.CompletionLabels.TakeWhile(label => label != named))
        {
            Ui.Press(window, "DOWN");
        }
        Assert.Equal(named, window.Assist.CompletionChosen);
        Ui.Press(window, key);
        Ui.Settle();
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Assert.Equal(existingEquals ? "    [System.Obsolete(DiagnosticId = \"Old\")]" : "    [System.Obsolete(DiagnosticId =)]", line);
        Assert.Equal(line.IndexOf("DiagnosticId", StringComparison.Ordinal) + named.Length, editor.CaretPosition.Column);
        window.Close();
    }

    /// <summary>A declaration keyword chosen by either key takes the separator already after the caret (the first) or
    /// writes a space where none is (the next two), so the caret stands after one separator.</summary>
    private void Declaration_completion_leaves_the_caret_after_one_separator(string key, string existing)
    {
        string path = project.PathOf("Program.cs");
        File.WriteAllText(path, existing + "\n" + File.ReadAllText(path));
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.GoTo(new TextPosition(0, 0));
        Type("pu");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "public", "public offered");
        string before = editor.Document.Snapshot.ToString();
        Ui.Press(window, key);
        Ui.Settle();
        string prefix = "public" + existing;
        Assert.StartsWith(prefix + "\nclass Program", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(prefix.Length, editor.Selections.Primary.Caret);

        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Assert.Equal(before, editor.Document.Snapshot.ToString());
        Ui.Press(window, Ui.Shortcut(EditorActions.Redo));
        Assert.StartsWith(prefix + "\nclass Program", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(prefix.Length, editor.Selections.Primary.Caret);

        Type("sta");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "static", "static offered after public");
        Ui.Press(window, key);
        Ui.Settle();
        prefix += "static ";
        Assert.StartsWith(prefix + "\nclass Program", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(prefix.Length, editor.Selections.Primary.Caret);
        Type("cla");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "class", "class offered after static");
        Ui.Press(window, key);
        Ui.Settle();
        prefix += "class ";
        Assert.StartsWith(prefix + "\nclass Program", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal(prefix.Length, editor.Selections.Primary.Caret);
        window.KeyTextInput("Example {}");
        Ui.Settle();
        Assert.StartsWith(prefix + "Example {}", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();

        void Type(string letters)
        {
            foreach (char letter in letters)
            {
                window.KeyTextInput(letter.ToString());
                Ui.Settle();
            }
        }
    }

    /// <summary>Where the code takes a type, the name typed lists first what is of that type, as Roslyn's own matching says
    /// it fits, ahead of a name that begins as well, which stays listed: in an if's condition, an argument and a return.</summary>
    private void Completion_lists_first_the_names_of_the_type_the_place_takes_keeping_the_rest()
    {
        MainWindow window = project.Open("Fits.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Fits(window, editor, "if (", "a", "aef", "abc", "an if's condition takes a bool");
        Fits(window, editor, "Name(", "c", "caption", "count", "Name takes a string");
        Fits(window, editor, "return ", "a", "aef", "abc", "Check returns a bool");
        Assert.True(editor.Document.Snapshot.ToString() == FitsCode, "Completion changed the file it only listed names for");
        window.Close();
    }

    /// <summary>Types a letter after some text, where the list first shows <paramref name="fitting"/>, keeps
    /// <paramref name="other"/> (a name that begins as well, of another type) and lets the letter go again.</summary>
    private static void Fits(MainWindow window, CodeEditor editor, string after, string letter, string fitting, string other, string why)
    {
        Ui.PlaceAfter(editor, after);
        Ui.Settle();
        window.KeyTextInput(letter);
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == letter && window.Assist.CompletionLabels.Contains(other), $"the list after '{after}{letter}'");
        IReadOnlyList<string> labels = window.Assist.CompletionLabels;
        Assert.True(labels.FirstOrDefault() == fitting,
            $"After '{after}{letter}' the list showed [{string.Join(", ", labels.Take(5))}] first, not {fitting}, though {why}: the name meant would be a key further down");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK");
        Ui.Settle();
    }

    /// <summary>A method whose places each take a type, one local of that type and one of another type beginning as its
    /// name does.</summary>
    private const string KindsCode = "using System.Linq;\n\nstatic class Kinds\n{\n    public static int Totalled(int first, string label)\n    {\n"
        + "        var count = label.Length;\n        double half = first / 2.0;\n        int Doubled(int value, bool loud) => loud ? value * 2 : value;\n"
        + "        _ = label;\n        _ = Described(label, label, first);\n        _ = System.Math.Max(1, 2);\n        return Doubled(count, false) + (int)half;\n    }\n\n"
        + "    public static string Described(string firstName, string lastName, int yearOfBirth, bool includeTheMiddleInitial = false) => firstName;\n}\n";

    /// <summary>Parameter Info at a call of several overloads shows each: those the arguments so far no longer fit set back,
    /// the parameter the caret's argument fills strong in each that fits. An argument typed that only one overload takes sets
    /// the other back, and once it is gone again both fit; an argument not typed yet is no evidence against either. Arguments
    /// fit as C# converts them: a constant a smaller number's overload, strings a params span of strings, an interpolated
    /// string its handler. A call closed inside another hands the card back to the outer call, whose own close ends it.</summary>
    private void Parameter_info_shows_every_overload_setting_back_those_the_arguments_no_longer_fit()
    {
        MainWindow window = project.Open("Kinds.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "_ = label;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("_ = new System.ArgumentNullException(label, );");
        Ui.Press(window, "LEFT");
        Ui.Press(window, "LEFT");
        Ui.Run(window, AppActions.ParameterInfo);
        const string Message = "(string? paramName, string? message)";
        const string Inner = "(string? message, System.Exception? innerException)";
        Ui.WaitFor(() => Overloads().Count == 4, () => $"the four constructors after the comma: {window.Assist.SignatureText}");
        Assert.Equal([("()", false, null), ("(string? paramName)", false, null), (Message, true, "string? message"), (Inner, true, "System.Exception? innerException")],
            Overloads().OrderBy(o => o.Code.Length).Select(o => (o.Code[o.Code.IndexOf('(', StringComparison.Ordinal)..], o.Fits, o.Strong)));

        // Next and Previous Overload step the chosen one, on the keys the keymap in force gives them there.
        string chosen = Chosen();
        Ui.Press(window, Keys(AppActions.NextOverload));
        Ui.WaitFor(() => Chosen() != chosen, () => $"another overload chosen after Next Overload's keys: {window.Assist.SignatureText}");
        Assert.Contains("Overload ", window.Assist.SignatureText, StringComparison.Ordinal);
        Ui.Press(window, Keys(AppActions.PreviousOverload));
        Ui.WaitFor(() => Chosen() == chosen, () => $"the first overload chosen again after Previous Overload's keys: {window.Assist.SignatureText}");
        Assert.Equal("        _ = new System.ArgumentNullException(label, );", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line));

        window.KeyTextInput("new System.Exception()");
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => Overloads().Any(o => o.Code.EndsWith(Message, StringComparison.Ordinal) && !o.Fits),
            () => $"the message overload set back after an exception argument: {string.Join(" | ", Overloads())}");
        Assert.Contains(Overloads(), o => o.Code.EndsWith(Inner, StringComparison.Ordinal) && o.Fits && o.Strong == "System.Exception? innerException");

        foreach (char _ in "new System.Exception()")
        {
            Ui.Press(window, "shift LEFT");
        }
        Ui.Press(window, "BACKSPACE");
        Assert.Equal("        _ = new System.ArgumentNullException(label, );", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line));
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => Overloads().Count(o => o.Fits) == 2, () => $"both two-argument overloads fitting once the argument is gone: {string.Join(" | ", Overloads())}");

        Ui.PlaceAfter(editor, "System.Math.Max(1, 2");
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => Overloads().Any(o => o.Code.EndsWith("(byte val1, byte val2)", StringComparison.Ordinal)), () => $"Math.Max's overloads: {window.Assist.SignatureText}");
        Assert.True(Overloads().All(o => o.Fits), $"Two constants set back the overloads of the numbers they convert to: {string.Join(" | ", Overloads().Where(o => !o.Fits))}");
        Ui.Press(window, "END");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("_ = string.Join(\",\", label, label);");
        Ui.Press(window, "LEFT");
        Ui.Press(window, "LEFT");
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => Overloads().Any(o => o.Code.Contains("ReadOnlySpan<string?>", StringComparison.Ordinal)), () => $"string.Join's overloads: {window.Assist.SignatureText}");
        Assert.True(Overloads().Any(o => o.Code.Contains("ReadOnlySpan<string?>", StringComparison.Ordinal) && o.Fits),
            $"Strings set back the overload whose params span of strings takes them: {string.Join(" | ", Overloads())}");
        Ui.Press(window, "END");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Diagnostics.Debug.Assert(true, $\"{label}\");");
        Ui.Press(window, "LEFT");
        Ui.Press(window, "LEFT");
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => Overloads().Any(o => o.Code.EndsWith("AssertInterpolatedStringHandler message)", StringComparison.Ordinal)), () => $"Debug.Assert's overloads: {window.Assist.SignatureText}");
        Assert.True(Overloads().Any(o => o.Code.EndsWith("AssertInterpolatedStringHandler message)", StringComparison.Ordinal) && o.Fits),
            $"An interpolated string set back the overload whose handler takes it: {string.Join(" | ", Overloads())}");

        Ui.Press(window, "END");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("_ = System.Math.Max(");
        window.KeyTextInput("System.Math.Abs(");
        window.KeyTextInput("1");
        Ui.WaitFor(() => Overloads().Any(o => o.Code.Contains(".Abs(", StringComparison.Ordinal)), () => $"Math.Abs's overloads inside Max's call: {window.Assist.SignatureText}");
        window.KeyTextInput(")");
        Ui.WaitFor(() => Overloads().Any(o => o.Code.EndsWith("(byte val1, byte val2)", StringComparison.Ordinal)),
            () => $"Math.Max's overloads again once Abs's call closed inside it, not {window.Assist.SignatureText ?? "no card"}");
        window.KeyTextInput(")");
        Ui.WaitFor(() => window.Assist.SignatureCard is null, () => $"the card gone once Max's call closed, not {window.Assist.SignatureText}");
        window.Close();

        // The overload the card marks chosen.
        string Chosen() => window.Assist.SignatureCard?.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Chosen))
            .SelectMany(b => b.GetVisualDescendants().OfType<ColouredCode>()).Select(c => c.Code).FirstOrDefault() ?? string.Empty;

        // Each overload the card shows: its code, whether the arguments still fit it, and the parameter drawn strong.
        List<(string Code, bool Fits, string? Strong)> Overloads() =>
            window.Assist.SignatureCard?.GetVisualDescendants().OfType<ColouredCode>()
                .Select(c => (c.Code, !c.Classes.Contains(StyleClasses.Unfit), c.Marked is { } m ? c.Code.Substring(m.Start, m.Length) : null)).ToList() ?? [];
    }

    /// <summary>The keys an action runs from where it is pressed, as the keymap in force has them.</summary>
    private static string Keys(string action) =>
        AppActions.Keymap().ShortcutsFor(AppActions.KeysIn(action)).First(s => s.Second is null).Notation();

    private const string FitsCode = "static class Fits\n{\n    public static bool Check()\n    {\n        int abc = 1;\n        bool aef = true;\n"
        + "        int count = 2;\n        string caption = \"\";\n        if ()\n        {\n        }\n        Name();\n        return ;\n    }\n\n"
        + "    private static void Name(string text)\n    {\n    }\n}\n";

    private void CamelHumps_typed_offer_the_names_they_start_after_the_prefix_matches_and_enter_writes_the_one_chosen()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static void Main()\n    {");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("var built = new System.Text.");
        Ui.Settle();
        Type("SB");
        Assert.True(window.Assist.CompletionLabels.FirstOrDefault() == "StringBuilder",
            $"Typing SB offered [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}], not StringBuilder first");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("new System.Text.StringBuilder", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // A lower-case hump then capitals, as gTOD names GetTypeOrDefault.
        window.KeyTextInput("();");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int at = \"text\".");
        Ui.Settle();
        Type("lIO");
        Assert.True(window.Assist.CompletionLabels.Take(2).SequenceEqual(["LastIndexOf", "LastIndexOfAny"]),
            $"Typing lIO offered [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}], not LastIndexOf then LastIndexOfAny");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("int at = \"text\".LastIndexOf()", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // A name the humps start comes after every name the letters begin.
        window.KeyTextInput("'x'");
        window.KeyTextInput(")");
        window.KeyTextInput(";");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("bool plain = \"text\".");
        Ui.Settle();
        Type("IN");
        IReadOnlyList<string> labels = window.Assist.CompletionLabels;
        int humps = labels.ToList().IndexOf("IsNormalized");
        Assert.True(humps > 0 && labels.Take(humps).All(l => l.StartsWith("IN", StringComparison.OrdinalIgnoreCase))
            && !labels.Skip(humps).Any(l => l.StartsWith("IN", StringComparison.OrdinalIgnoreCase)),
            $"Typing IN offered [{string.Join(", ", labels.Take(8))}], not IsNormalized after every name beginning with In");
        Ui.Press(window, "ESCAPE");

        // An acronym's last capital before lower case starts a word, as IPAddress is IP + Address.
        window.KeyTextInput(";");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("var address = System.Net.");
        Ui.Settle();
        Type("IA");
        Assert.True(window.Assist.CompletionLabels.Contains("IPAddress"),
            $"Typing IA offered [{string.Join(", ", window.Assist.CompletionLabels.Take(5))}], not IPAddress");
        Ui.Press(window, "ESCAPE");
        window.Close();

        void Type(string letters)
        {
            foreach (char letter in letters)
            {
                window.KeyTextInput(letter.ToString());
                Ui.Settle();
            }
        }
    }

    /// <summary>A list the server is still answering is let go by an Escape, and by the caret moved back over the dot that
    /// asked for it: its answer, held until then, opens nothing, where System's members opened after the Escape, or at the
    /// caret before the dot.</summary>
    private void A_list_still_asked_for_is_let_go_by_an_escape_or_the_caret_moved_out_of_its_name()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        string method = server.ExtensionProblem is null ? Passenger.Roslyn.Extension.ExtensionMethods.Completion : Passenger.Lsp.LspMethods.Completion;
        Ui.PlaceAfter(editor, "static void Main()\n    {\n");
        window.KeyTextInput("        System");
        Ui.Settle();
        foreach (string away in (string[])["ESCAPE", "LEFT"])
        {
            TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task held = server.HoldNextAnswer(method, _ => true, answered.Task);
            try
            {
                window.KeyTextInput(".");
                Ui.AwaitPumped(held, "System's members, answered and held");
                Ui.Press(window, away, settle: false);
            }
            finally
            {
                answered.SetResult();
            }
            Ui.Settle();
            Assert.False(window.Assist.IsCompletionOpen,
                $"System's members asked for and let go by {away} opened once their answer came: {string.Join(", ", window.Assist.CompletionLabels.Take(5))}");
            Ui.Press(window, away == "LEFT" ? "DELETE" : "BACK_SPACE");
        }
        window.Close();
    }

    private void A_commit_character_types_the_chosen_item_before_it_and_the_items_documentation_shows_beside_the_list()
    {
        Assert.SkipWhen(project.ReferencePackDownloaded,
            $"No installed .NET SDK has the {TestProjects.Framework} reference pack the newest SDK targets, so restore took it from NuGet, whose "
            + "package carries no documentation for completion to show (install the .NET 10 SDK of that version)");
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static void Main()\n    {\n");
        window.KeyTextInput("        System.");
        Ui.Settle();
        window.KeyTextInput("C");
        Ui.Settle();
        window.KeyTextInput("onso");
        Ui.Settle();
        Assert.Equal("Console", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.WaitFor(() => window.Assist.ItemDocumentation?.Contains("standard input", StringComparison.Ordinal) == true, "the chosen item's documentation");

        window.KeyTextInput(".");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString().Contains("System.Console.", StringComparison.Ordinal), editor.Document.Snapshot.ToString());
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, "Console's members");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "BackgroundColor" && window.Assist.ItemDocumentation?.Contains("BackgroundColor", StringComparison.Ordinal) == true,
            () => $"the card of BackgroundColor, first of the members the dot listed, before the choice moves (the card says {window.Assist.ItemDocumentation ?? "nothing"})");
        window.KeyTextInput("WriteL");
        Ui.Settle();
        window.KeyTextInput("(");
        Ui.Settle();
        Assert.Contains("System.Console.WriteLine(", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.False(window.Assist.IsCompletionOpen);
        Assert.Null(window.Assist.ItemDocumentation);
        window.Close();
    }

    private void A_member_chosen_from_the_list_a_dot_opened_with_nothing_typed_leaves_the_caret_after_it()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Console");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "BackgroundColor", "Console's members after its dot, BackgroundColor first");

        Ui.Press(window, "ENTER");
        Ui.Settle();
        window.KeyTextInput(" = System.ConsoleColor.DarkBlue;");
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Assert.True(line == "        System.Console.BackgroundColor = System.ConsoleColor.DarkBlue;",
            $"What was typed after choosing BackgroundColor with nothing typed after the dot did not follow it: '{line}'");
        Assert.Equal(line.Length, editor.CaretPosition.Column);
        window.Close();
    }

    private void An_extension_method_chosen_after_a_dot_adds_its_using_above_and_leaves_the_caret_after_the_method()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int[] numbers = [3, 1, 2];");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int largest = numbers");
        // Roslyn lists the methods of namespaces not imported once it has indexed them, so the dot is typed again until it has.
        Ui.WaitFor(() =>
        {
            if (window.Assist.CompletionLabels.Contains("Max"))
            {
                return true;
            }
            Ui.Press(window, "ESCAPE");
            if (editor.Document.Snapshot.ToString().Contains("numbers.", StringComparison.Ordinal))
            {
                Ui.Press(window, "BACK_SPACE");
            }
            Ui.Hold(TimeSpan.FromMilliseconds(250), "a pause before the dot is typed again, as a person retries");
            window.KeyTextInput(".");
            Ui.Settle();
            return false;
        }, "LINQ's Max, of a namespace Program.cs does not import, among the array's members");
        for (int down = window.Assist.CompletionLabels.ToList().IndexOf("Max"); down > 0; down--)
        {
            Ui.Press(window, "DOWN");
        }

        Ui.Press(window, "ENTER");
        Ui.Settle();
        // Max's overloads taking a selector put the caret between its call's brackets, which ')' types over.
        window.KeyTextInput(")");
        window.KeyTextInput(";");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.StartsWith("using System.Linq;\n", StringComparison.Ordinal), $"Choosing Max did not add the using it needs above:\n{text}");
        string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Assert.True(line == "        int largest = numbers.Max();",
            $"What was typed after choosing Max, its using added above, did not follow it: '{line}'\n{text}");
        Assert.Equal(line.Length, editor.CaretPosition.Column);
        window.Close();
    }

    /// <summary>A method chosen by Enter is written with its call's brackets: the caret between them where an overload
    /// takes arguments, its signature shown, and after them where none does, its name typed whole too, a '(' or ')' typed
    /// straight after as a hand used to typing them does taken as typed already, never doubling them; a method given where
    /// a delegate is asked for (an Action, a System.Delegate) stays a method group, without them.</summary>
    private void A_method_chosen_from_the_list_is_written_called_the_caret_inside_its_brackets_when_it_takes_arguments()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();

        Ui.Press(window, "ENTER");
        window.KeyTextInput("int doubled = Helper.");
        Ui.Settle();
        window.KeyTextInput("Tw");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Twice", "Helper.Twice among its members");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(Line() == "        int doubled = Helper.Twice()" && editor.CaretPosition.Column == Line().Length - 1,
            $"Twice, which takes a value, was not written called with the caret between its brackets: '{Line()}', the caret at {editor.CaretPosition.Column}");
        window.KeyTextInput("(");
        Assert.True(Line() == "        int doubled = Helper.Twice()" && editor.CaretPosition.Column == Line().Length - 1,
            $"A '(' typed straight after Twice's own was not taken as typed already: '{Line()}', the caret at {editor.CaretPosition.Column}");
        Ui.WaitFor(() => window.Assist.SignatureText?.Contains("int value", StringComparison.Ordinal) == true, "Twice's signature");
        window.KeyTextInput("3");
        window.KeyTextInput(")");
        window.KeyTextInput(";");
        Assert.Equal("        int doubled = Helper.Twice(3);", Line());

        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Console.");
        Ui.Settle();
        window.KeyTextInput("Clea");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Clear", "Console.Clear among the members");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(Line() == "        System.Console.Clear()" && editor.CaretPosition.Column == Line().Length,
            $"Clear, which takes nothing, was not written called with the caret after its brackets: '{Line()}', the caret at {editor.CaretPosition.Column}");
        window.KeyTextInput("(");
        window.KeyTextInput(")");
        window.KeyTextInput(";");
        Assert.True(Line() == "        System.Console.Clear();", $"'()' typed straight after Clear's own were not taken as typed already: '{Line()}'");

        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Action clear = System.Console.");
        Ui.Settle();
        window.KeyTextInput("Clea");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Clear", "Console.Clear among the members");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(Line() == "        System.Action clear = System.Console.Clear" && editor.CaretPosition.Column == Line().Length,
            $"Clear given to an Action was written as a call rather than the method itself: '{Line()}'");
        window.KeyTextInput(";");

        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Delegate any = System.Console.");
        Ui.Settle();
        window.KeyTextInput("Clea");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Clear", "Console.Clear among the members");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(Line() == "        System.Delegate any = System.Console.Clear",
            $"Clear given to a System.Delegate was written as a call rather than the method itself: '{Line()}'");
        window.KeyTextInput(";");

        // A name typed whole changes nothing as it goes in, and is called all the same.
        Ui.Press(window, "ENTER");
        window.KeyTextInput("System.Console.");
        Ui.Settle();
        window.KeyTextInput("Clear");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Clear", "Console.Clear among the members");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.True(Line() == "        System.Console.Clear()" && editor.CaretPosition.Column == Line().Length,
            $"Clear, typed whole and chosen, was not written called with the caret after its brackets: '{Line()}', the caret at {editor.CaretPosition.Column}");
        window.KeyTextInput(")");
        window.KeyTextInput(")");
        Assert.True(Line() == "        System.Console.Clear())", $"A ')' typed straight after Clear's own was not taken as typed already, or the next one was too: '{Line()}'");
        window.Close();
    }

    /// <summary>A method chosen by Enter is written at once, its brackets following as the server says it is called there,
    /// and one Undo takes back both; a key typed before that answer came is what follows the name, and no brackets come.</summary>
    private void A_method_chosen_is_written_at_once_its_brackets_following_in_its_step_of_undo_unless_typed_on_before_they_came()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();

        Ui.Press(window, "ENTER");
        window.KeyTextInput("int tripled = Helper.");
        Ui.Settle();
        window.KeyTextInput("Tw");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Twice", "Helper.Twice among its members");
        TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = window.CSharpService!.Session!.Server.HoldNextAnswer(Passenger.Lsp.LspMethods.DispatchDocumentExtensionMessage, AsksCall, answered.Task);
        try
        {
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => held.IsCompleted, "the server's answer whether Twice is called there, held");
            Assert.True(Line() == "        int tripled = Helper.Twice" && editor.CaretPosition.Column == Line().Length,
                $"Twice was not written at once, before the server said whether it is called there: '{Line()}', the caret at {editor.CaretPosition.Column}");
            window.KeyTextInput("(");
            window.KeyTextInput("3");
            window.KeyTextInput(")");
            window.KeyTextInput(";");
        }
        finally
        {
            answered.SetResult();
        }
        Ui.Settle();
        Assert.Equal("        int tripled = Helper.Twice(3);", Line());

        Ui.Press(window, "ENTER");
        window.KeyTextInput("int doubled = Helper.");
        Ui.Settle();
        window.KeyTextInput("Tw");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Twice", "Helper.Twice among its members");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("        int doubled = Helper.Twice()", Line());
        Ui.Press(window, Ui.Shortcut(EditorActions.Undo));
        Assert.True(Line() == "        int doubled = Helper.Tw",
            $"One Undo after Twice was written called left '{Line()}', where it takes back the name and its brackets together");
        window.Close();
    }

    /// <summary>How Passenger's extension is asked whether a method just written is called.</summary>
    private static bool AsksCall(object parameters) =>
        parameters is Passenger.Lsp.RoslynExtension.DispatchExtensionMessageParams { MessageName: Passenger.Roslyn.Extension.Handlers.CompletionCall };

    /// <summary>With Settings' parentheses for a method chosen from completion turned off, a method chosen by Enter is
    /// written as its name alone, the caret after it.</summary>
    private void With_parentheses_turned_off_a_method_chosen_from_the_list_is_written_as_its_name_alone()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Line() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
        void Change(Func<AppSettings, AppSettings> change) => window.ApplySettings(change(window.Settings));
        try
        {
            Change(s => s with { CompletionParentheses = false });
            Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
            editor.Focus();
            Ui.Press(window, "ENTER");
            window.KeyTextInput("int doubled = Helper.");
            Ui.Settle();
            window.KeyTextInput("Tw");
            Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Twice", "Helper.Twice among its members");
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.True(Line() == "        int doubled = Helper.Twice" && editor.CaretPosition.Column == Line().Length,
                $"With parentheses turned off, Twice was not written as its name alone with the caret after it: '{Line()}', the caret at {editor.CaretPosition.Column}");
        }
        finally
        {
            Change(s => s with { CompletionParentheses = true });
            window.Close();
        }
    }

    /// <summary>What Roslyn's server package carries under Visual Studio's terms rather than an open-source licence and
    /// Passenger.Lsp.csproj leaves out of roslyn/ (RoslynServerLeftOut), since nothing Passenger asks of the server loads them.</summary>
    private static readonly string[] LeftOut =
    [
        "Microsoft.ServiceHub.Client", "Microsoft.VisualStudio.Debugger.Contracts", "Microsoft.VisualStudio.RemoteControl",
        "Microsoft.VisualStudio.RpcContracts",
    ];

    /// <summary>Typing after a dot opens completion, filtered as one types, its card listing an item's first overloads and
    /// how many more before its words, and Enter commits the item chosen, from a language server that has none of the
    /// Visual Studio assemblies Passenger leaves out.</summary>
    private void Typing_after_a_dot_completes_filtered_as_you_type_and_enter_commits_it_without_the_visual_studio_assemblies_passenger_leaves_out()
    {
        string roslyn = Path.Combine(AppContext.BaseDirectory, "roslyn");
        string[] carried = [.. Directory.EnumerateFiles(roslyn, "*.dll", SearchOption.AllDirectories)
            .Where(file => LeftOut.Any(name => Path.GetFileName(file).StartsWith(name + ".", StringComparison.Ordinal)))];
        Assert.True(carried.Length == 0, $"Passenger carries Visual Studio's own assemblies beside the language server, whose terms are not open source:\n{string.Join('\n', carried)}");

        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static void Main()\n    {\n");
        window.KeyTextInput("        System.Console.");
        Ui.Settle();
        window.KeyTextInput("W");
        Ui.Settle();
        window.KeyTextInput("riteL");
        Ui.Settle();
        Assert.True(window.Assist.IsCompletionOpen);
        Assert.Equal("WriteLine", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.WaitFor(() => window.Assist.ItemDocumentation is { Length: > 0 }, "WriteLine's documentation");
        string card = window.Assist.ItemDocumentation!;
        int listed = window.Assist.ItemCard!.GetVisualDescendants().OfType<ColouredCode>().Count();
        Assert.True(listed == 8 && card.Contains(" more overloads", StringComparison.Ordinal) && card.Contains("Writes", StringComparison.Ordinal),
            $"WriteLine's card lists {listed} overloads, not the first eight and how many more, its words after them out of the editor's room: {card}");

        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.False(window.Assist.IsCompletionOpen);
        Assert.Contains("System.Console.WriteLine", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        // Razor's start is where the server first loads Visual Studio's assemblies; a failed load logs the name.
        string path = window.CSharpService!.Session!.Server.LogPath;
        string log = string.Empty;
        Ui.WaitFor(() => (log = Ui.Shared(path)).Contains("Razor extension startup finished.", StringComparison.Ordinal), "the language server's Razor support to start");
        string[] looked = [.. log.Split('\n').Where(line => LeftOut.Any(name => line.Contains(name, StringComparison.Ordinal)))];
        Assert.True(looked.Length == 0, $"The language server looked for an assembly Passenger leaves out, so something it does needs it:\n{string.Join('\n', looked)}");
        window.Close();
    }

    /// <summary>Find Usages says so with no search yet and shows nothing else; it groups usages by what they do,
    /// project and file, previews the chosen one beside a divider that drags, keeps each search, goes to a usage on
    /// Enter, lists a usage in a closed UTF-16 file at its line with its code, and lists a declaration once where a
    /// primary constructor or a record's property is declared with it.</summary>
    private void Find_usages_groups_them_previews_the_chosen_one_keeps_each_search_and_lists_each_place_once_at_its_line()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        FindView find = window.Find;
        Ui.Run(window, AppActions.ShowFind);
        Avalonia.Controls.Border[] Rules() => [.. find.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Where(b => b.IsEffectivelyVisible && b.BorderThickness.Bottom > 0 && b.TemplatedParent is null)];
        Assert.True(Rules().Length == 0, "The empty Find window shows a row with a rule under it, before any search has a tab there");
        Ui.PlaceAfter(editor, "int tot");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Rows.Count > 0, "the usages in the Find window");
        Assert.True(Rules().Length > 0, "A search's tab has no row of its own over the usages");
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Find));
        Assert.Equal(["Declaration", "Write access", "Read access"], find.Rows.Where(r => r.Depth == 0).Select(r => r.Label));
        Assert.All(find.Rows.Where(r => r.Depth == 1), r => Assert.Equal("Sample", r.Label));
        Assert.All(find.Rows.Where(r => r.Depth == 2), r => Assert.Equal("Loop.cs", r.Label));
        Assert.Equal(["int total = 0;", "total += i * 2;", "return total;"], find.Rows.Where(r => r.Usage is not null).Select(r => r.Label));

        Assert.Equal("int total = 0;", find.Selected?.Label);
        Assert.Contains("for (int i = 0", find.Preview.Document.Snapshot.ToString(), StringComparison.Ordinal);
        TextRange marked = Assert.Single(find.Preview.Highlights);
        Assert.Equal("total", find.Preview.Document.Snapshot.GetText(marked.Start, marked.Length));

        Avalonia.Controls.ListBox usageList = find.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single();
        (double usagesWidth, double previewWidth) = (usageList.Bounds.Width, find.Preview.Bounds.Width);
        Avalonia.Controls.Control divider = find.GetVisualDescendants().OfType<Passenger.App.ToolWindows.PaneSplit>().Single().Divider;
        Point grip = divider.TranslatePoint(new Point(divider.Bounds.Width / 2, divider.Bounds.Height / 2), window)!.Value;
        window.MouseDown(grip, Avalonia.Input.MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(grip - new Point(15 * step, 0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
        }
        window.MouseUp(grip - new Point(150, 0), Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.True(Math.Abs(usageList.Bounds.Width - (usagesWidth - 150)) < 2 && Math.Abs(find.Preview.Bounds.Width - (previewWidth + 150)) < 2,
            $"The usages and the preview were {usagesWidth} and {previewWidth} wide, and {usageList.Bounds.Width} and {find.Preview.Bounds.Width} once the divider between them was dragged 150 px left");

        window.OpenFile(project.PathOf("Program.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Helper.Twi");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Shown == "Usages of Twice", "the second search");
        Assert.Equal(["Usages of total", "Usages of Twice"], find.Searches);
        Assert.Equal(["Declaration", "Invocation"], find.Rows.Where(r => r.Depth == 0).Select(r => r.Label));
        find.ShowSearch("Usages of total");
        Assert.Equal(3, find.Rows.Count(r => r.Usage is not null));

        find.Selected = find.Rows.Last(r => r.Usage is not null);
        find.FocusRows();
        Ui.Press(window, "ENTER");
        Assert.Equal("Loop.cs", window.EditorArea.Active?.Name);
        Assert.Equal(new TextPosition(9, 15), window.EditorArea.CodeEditor.CaretPosition);

        string wide = project.PathOf("Wide.cs");
        Assert.True(File.ReadAllBytes(wide) is [0xFF, 0xFE, ..], "Wide.cs is not written in UTF-16");
        window.OpenFile(project.PathOf("Area.cs"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "static int O");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Shown == "Usages of Of" && find.Rows.Any(r => r.Usage?.Path == PathNames.Canonical(wide)), "the usage in the UTF-16 file");
        Usage usage = find.Rows.Select(r => r.Usage).OfType<Usage>().Single(u => u.Path == PathNames.Canonical(wide));
        Assert.True((usage.Line, usage.Column, usage.LineText) == (4, 20, "        return Area.Of(2, 2);"),
            $"Find Usages listed the UTF-16 file's usage at {usage.Line + 1}:{usage.Column + 1} as '{usage.LineText}', not where it is with its line of code");

        window.OpenFile(project.PathOf("Pairs.cs"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        // Each declared where another symbol is, which the server names once for each.
        foreach ((string at, string title, string declaration) in new[]
        {
            ("class Coun", "Usages of Counter", "class Counter(int start)"),
            ("record Pa", "Usages of Pair", "record Pair(int Left, int Right)"),
            ("pair.Lef", "Usages of Left", "record Pair(int Left, int Right)"),
        })
        {
            Ui.PlaceAfter(editor, at);
            Ui.Run(window, AppActions.FindUsages);
            Ui.WaitFor(() => find.Shown == title, $"the {title}");
            List<FindRow> declared = [.. Visible(find, "Declaration").Where(r => r.Usage is not null)];
            Assert.True(declared.Count == 1 && declared[0].Label == declaration,
                $"The Find window lists the declaration of {title[10..]} {declared.Count} times, where Rider lists each place once: {string.Join("; ", declared.Select(r => $"{r.Label} ({r.Detail})"))}");
            List<Usage> usages = [.. find.Rows.Select(r => r.Usage).OfType<Usage>()];
            Assert.True(usages.DistinctBy(u => (u.Path, u.Line, u.Column)).Count() == usages.Count,
                $"The Find window lists a place of {title[10..]} more than once: {string.Join("; ", usages.Select(u => $"{u.Line + 1}:{u.Column + 1} {u.LineText.Trim()}"))}");
        }
        TreeKeysCheck.Press(window, find.Rows, find.Rows[0], "the Find window");
        window.Close();

        // The rows under a group of what usages do, to the next group.
        static IEnumerable<FindRow> Visible(FindView find, string group) =>
            find.Rows.SkipWhile(r => r.Depth != 0 || r.Label != group).Skip(1).TakeWhile(r => r.Depth != 0);
    }

    /// <summary>Show Usages, from its keys, lists the usages Find Usages finds in a popup under the caret, framed as every
    /// popup is, the declaration first with what each does and the usage in its line; Enter goes to the one chosen, and its
    /// link hands the usages to the Find window.</summary>
    private void Show_usages_lists_them_in_a_popup_under_the_caret_goes_to_the_one_chosen_and_hands_them_to_the_find_window()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int tot");
        Point caret = editor.TranslatePoint(editor.PointOf(editor.Selections.Primary.Caret), window)!.Value;
        Ui.Press(window, window.Actions.Keymap.ShortcutsFor(AppActions.ShowUsages)[0].Notation());
        Ui.WaitFor(() => window.Popup is UsagesPopup { Rows.Count: 3 }, () => $"the usages of total in Show Usages' popup ({window.Status})");
        UsagesPopup usages = (UsagesPopup)window.Popup!;
        Assert.Equal("Usages of total  3 usages", usages.Title);
        Assert.Equal([(4, "int total = 0;"), (7, "total += i * 2;"), (9, "return total;")], usages.Rows.Select(u => (u.Line, u.LineText.Trim())));
        Assert.Equal(Languages.CSharp.UsageKind.Declaration, usages.Rows[0].Kind);
        Border frame = PopupFrame.FrameOf(usages)!;
        UiRulesTests.HoldsToPopupRules("Show Usages", frame);
        Point top = frame.TranslatePoint(default, window)!.Value;
        Assert.True(top.Y > caret.Y && Math.Abs(top.X - caret.X) < PopupFrame.Wide,
            $"Show Usages' popup opened at {top}, not under the caret at {caret}");
        Assert.False(window.Find.Searches.Contains("Usages of total"), "Show Usages listed the usages in the Find window as well as its popup");

        Ui.Press(window, "DOWN");
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.True(window.Popup is null, "Show Usages' popup stayed open once a usage was chosen");
        Assert.Equal(new TextPosition(9, 15), editor.CaretPosition);

        Ui.PlaceAfter(editor, "int tot");
        Ui.Run(window, AppActions.ShowUsages);
        Ui.WaitFor(() => window.Popup is UsagesPopup { Rows.Count: 3 }, "Show Usages' popup again");
        Ui.Click(((UsagesPopup)window.Popup!).FindWindow);
        Ui.WaitFor(() => window.Find.Shown == "Usages of total", "Open in Find Window to list the usages there");
        Assert.True(window.Popup is null, "Show Usages' popup stayed open once its usages went to the Find window");
        Assert.Equal(3, window.Find.Rows.Count(r => r.Usage is not null));
        window.Close();
    }

    private void The_error_stripe_marks_a_problem_in_its_colour_says_what_it_is_and_a_click_goes_there()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal((0, 0), editor.ProblemCount);
        Ui.PlaceAfter(editor, "        return total;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => editor.ProblemCount.Errors == 1, "the error's diagnostic");
        CodeEditor.StripeMark error = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Error);
        Assert.Contains("convert", error.Message, StringComparison.Ordinal);
        Assert.Contains(editor.StripeMarks, m => m.Kind == StripeMarkKind.Caret);

        Point at = editor.TranslatePoint(editor.StripePoint(error), window)!.Value;

        editor.GoTo(new TextPosition(0, 0));
        Ui.Settle();
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.Equal(editor.Document.Snapshot.GetLineFromOffset(error.Offset), editor.CaretPosition.Line);

        Avalonia.Media.Color colour = Ui.Pixel(window, at);
        Assert.True(colour.R > 180 && colour.G < 140 && colour.B < 140, $"The error's mark is {colour}, not the error colour");

        window.Close();
    }

    private void A_right_click_on_a_call_puts_the_caret_there_and_its_menu_renames_it_in_every_file_that_uses_it_and_goes_to_an_implementation()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        int call = editor.Document.Snapshot.ToString().IndexOf("Twice", StringComparison.Ordinal) + 2;
        Ui.RightClick(editor, editor.PointOf(call) + new Vector(1, editor.LineHeight / 2));
        Assert.InRange(editor.Selections.Primary.Caret, call - 1, call + 1);
        Ui.Choose(window.OpenContextMenu, "Refactor", "Rename…");
        window.KeyTextInput("Double");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("Helper.Double(", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("public static int Double(", File.ReadAllText(project.PathOf("Helper.cs")), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("Shapes.cs"));
        Ui.Settle();
        int sides = editor.Document.Snapshot.ToString().IndexOf("Sides", StringComparison.Ordinal) + 1;
        Ui.RightClick(editor, editor.PointOf(sides) + new Vector(1, editor.LineHeight / 2));
        Ui.Choose(window.OpenContextMenu, "Go To", "Go to Implementation");
        Ui.WaitFor(() => editor.CaretPosition.Line == 7, "the implementation in Square");
        Assert.Null(window.OpenContextMenu);
        window.Close();
    }

    private void A_reformat_started_before_switching_tabs_changes_only_its_own_file_and_breaks_lines_as_the_file_does()
    {
        MainWindow window = project.Open("Messy.cs");
        string helper = File.ReadAllText(project.PathOf("Helper.cs"));
        window.Perform(AppActions.Reformat);
        window.OpenFile(project.PathOf("Helper.cs"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument messy = window.EditorArea.Documents.First(d => d.Name == "Messy.cs");
        Ui.WaitFor(() => messy.Text.Snapshot.ToString().Contains("        List<string> names = [];", StringComparison.Ordinal), "Messy.cs reformatted");
        Assert.Equal("Helper.cs", window.EditorArea.Active!.Name);
        Assert.Equal(helper, window.EditorArea.CodeEditor.Document.Snapshot.ToString());

        // Roslyn writes the system's line break into whatever file it formats.
        window.OpenFile(project.PathOf("Crlf.cs"));
        Ui.Settle();
        Ui.Run(window, AppActions.Reformat);
        const string Formatted = "static class Crlf\r\n{\r\n    public static int One()\r\n    {\r\n        return 1;\r\n    }\r\n}\r\n";
        Ui.WaitFor(() => window.EditorArea.CodeEditor.Document.Snapshot.ToString() == Formatted, "Crlf.cs reformatted with its own line breaks");
        window.Close();
    }

    private void Reformat_code_with_a_method_selected_reformats_that_method_alone_and_leaves_the_code_around_it()
    {
        MainWindow window = project.Open("Ragged.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        int start = Ragged.IndexOf("    public static int Chosen()", StringComparison.Ordinal);
        int end = Ragged.LastIndexOf("    }", StringComparison.Ordinal) + "    }".Length;
        editor.Selections = SelectionSet.Single(new Selection(start, end));
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status is not null && window.Status.Contains("formatted", StringComparison.Ordinal), () => $"the reformat; the status says '{window.Status}'");
        string chosen = "    public static int Chosen()\n    {\n        int two = 2;\n        return two;\n    }\n}\n";
        Assert.True(editor.Document.Snapshot.ToString() == Ragged[..start] + chosen,
            $"Reformat Code with Chosen() selected left the file as:\n{editor.Document.Snapshot}");
        Assert.Equal("Reformatted", window.Status);
        window.Close();
    }

    /// <summary>The Explorer's Code menu: Reformat Code over a folder lays out the file open in its editor and the one not
    /// open on disk, and Code Cleanup over the project tidies every file of it, each as one command that one Undo in the
    /// Explorer takes back whole, saying what it changed; with dotnet format as the C# formatter, the folder's open file is
    /// saved first and takes what dotnet format wrote, one Undo taking all of it back too, the folder's watcher having
    /// taken it first.</summary>
    private void Reformat_code_over_a_folder_and_code_cleanup_over_the_project_from_the_explorer_each_change_every_file_as_one_step_of_undo()
    {
        string crooked = project.PathOf("Tidy/Crooked.cs");
        string spaced = project.PathOf("Tidy/Spaced.cs");
        MainWindow window = project.Open(crooked);
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        Passenger.App.Workspace.OpenDocument shown = window.EditorArea.Active!;
        Passenger.App.Explorer.ExplorerNode folder = ExplorerRow(window, project.PathOf("Tidy"), Passenger.App.Explorer.NodeKind.Folder);
        Ui.ChooseFromRowMenu(window, folder, "Code", "Reformat Code");
        Ui.WaitFor(() => window.Status?.StartsWith("Reformat Code changed", StringComparison.Ordinal) == true, () => $"the folder's reformat; the status says '{window.Status}'");
        Assert.Equal("Reformat Code changed 2 of the 2 files of Tidy", window.Status);
        Assert.True(shown.Text.Snapshot.ToString() == CrookedLaid, $"Reformat Code over Tidy left Crooked.cs, open, as:\n{shown.Text.Snapshot}");
        Assert.True(File.ReadAllText(spaced) == SpacedLaid, $"Reformat Code over Tidy left Spaced.cs, not open, as:\n{File.ReadAllText(spaced)}");

        Ui.ClickRow(window, ExplorerRow(window, project.PathOf("Tidy"), Passenger.App.Explorer.NodeKind.Folder));
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.Equal("Undo Reformat Code? It changes Crooked.cs and Spaced.cs.", asked.LastOrDefault());
        Assert.True(shown.Text.Snapshot.ToString() == Crooked && File.ReadAllText(spaced) == Spaced,
            $"One Undo in the Explorer left Crooked.cs as:\n{shown.Text.Snapshot}\nand Spaced.cs as:\n{File.ReadAllText(spaced)}");

        string messy = project.PathOf("Messy.cs");
        string messyBefore = File.ReadAllText(messy);
        // A folder of a project and no solution shows its files: the project's file stands for it.
        Passenger.App.Explorer.ExplorerNode sample = ExplorerRow(window, project.PathOf("Sample.csproj"), Passenger.App.Explorer.NodeKind.File);
        Ui.ChooseFromRowMenu(window, sample, "Code", "Code Cleanup");
        Ui.WaitFor(() => window.Status?.StartsWith("Code Cleanup changed", StringComparison.Ordinal) == true, () => $"the project's cleanup; the status says '{window.Status}'");
        string cleaned = File.ReadAllText(messy);
        Assert.True(cleaned.StartsWith("using System;\nusing System.Collections.Generic;\n", StringComparison.Ordinal) && !cleaned.Contains("System.Text", StringComparison.Ordinal)
            && cleaned.Contains("\n        return names.Count;\n", StringComparison.Ordinal) && File.ReadAllText(spaced) == SpacedLaid && shown.Text.Snapshot.ToString() == CrookedLaid,
            $"Code Cleanup over Sample ({window.Status}) left Messy.cs as:\n{cleaned}\nSpaced.cs as:\n{File.ReadAllText(spaced)}\nand Crooked.cs as:\n{shown.Text.Snapshot}");
        Ui.ClickRow(window, ExplorerRow(window, project.PathOf("Sample.csproj"), Passenger.App.Explorer.NodeKind.File));
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.StartsWith("Undo Code Cleanup? It changes ", asked.LastOrDefault(), StringComparison.Ordinal);
        Assert.True(File.ReadAllText(messy) == messyBefore && File.ReadAllText(spaced) == Spaced && shown.Text.Snapshot.ToString() == Crooked,
            $"One Undo of the project's Code Cleanup left Messy.cs as:\n{File.ReadAllText(messy)}\nSpaced.cs as:\n{File.ReadAllText(spaced)}\nand Crooked.cs as:\n{shown.Text.Snapshot}");

        // dotnet format as the C# formatter: it reads the disk, so the open file is saved first and takes what it wrote.
        window.ApplySettings(window.Settings with { CSharpFormatter = CSharpFormatter.DotnetFormat });
        // The folder's watcher takes what dotnet format wrote into the open file before the run hands it back, as it may.
        Func<IReadOnlyList<string>, bool, CancellationToken, Task<string?>> format = window.Assist.DotnetFormat!;
        window.Assist.DotnetFormat = async (files, whitespace, cancel) =>
        {
            TaskCompletionSource taken = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Taken(object? sender, DocumentChangedEventArgs change) => taken.TrySetResult();
            shown.Text.Changed += Taken;
            try
            {
                string? failed = await format(files, whitespace, cancel);
                return failed ?? (await Waiting.Within(taken.Task, TimeSpan.FromSeconds(30)) ? null : "the folder's watcher never took what dotnet format wrote into Crooked.cs");
            }
            finally
            {
                shown.Text.Changed -= Taken;
            }
        };
        Ui.ChooseFromRowMenu(window, ExplorerRow(window, project.PathOf("Tidy"), Passenger.App.Explorer.NodeKind.Folder), "Code", "Reformat Code");
        Ui.WaitFor(() => window.Status?.StartsWith("Reformat Code ", StringComparison.Ordinal) == true && window.Status != "Reformat Code: finding the files of Tidy…",
            () => $"the folder's reformat by dotnet format; the status says '{window.Status}'");
        Assert.Equal("Reformat Code changed 2 of the 2 files of Tidy", window.Status);
        Assert.True(shown.Text.Snapshot.ToString() == CrookedLaid && !shown.IsModified && File.ReadAllText(crooked) == CrookedLaid && File.ReadAllText(spaced) == SpacedLaid,
            $"dotnet format over Tidy left Crooked.cs, open, {(shown.IsModified ? "unsaved" : "saved")} as:\n{shown.Text.Snapshot}\non disk:\n{File.ReadAllText(crooked)}\nand Spaced.cs as:\n{File.ReadAllText(spaced)}");
        Ui.ClickRow(window, ExplorerRow(window, project.PathOf("Tidy"), Passenger.App.Explorer.NodeKind.Folder));
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.True(shown.Text.Snapshot.ToString() == Crooked && File.ReadAllText(spaced) == Spaced,
            $"One Undo of dotnet format's run left Crooked.cs as:\n{shown.Text.Snapshot}\nand Spaced.cs as:\n{File.ReadAllText(spaced)}");
        window.Assist.DotnetFormat = format;
        shown.Save();
        window.ApplySettings(window.Settings with { CSharpFormatter = CSharpFormatter.Passenger });
        window.Close();
    }

    /// <summary>A row of the Explorer, shown, by its kind and path.</summary>
    private static Passenger.App.Explorer.ExplorerNode ExplorerRow(MainWindow window, string path, Passenger.App.Explorer.NodeKind kind)
    {
        string full = PathNames.Canonical(path);
        window.ExplorerView.Reveal(full);
        Ui.Settle();
        return window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Kind == kind && r.Path == full)
            ?? throw new InvalidOperationException($"The Explorer does not show the {kind} {path}: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => $"{r.Kind} {r.Name}"))}]");
    }

    private const string Crooked = "class Crooked\n{\n    public static int One()\n    {\n          return 1;\n    }\n}\n";
    private const string CrookedLaid = "class Crooked\n{\n    public static int One()\n    {\n        return 1;\n    }\n}\n";
    private const string Spaced = "class Spaced\n{\n    public static int Two( )\n    {\n        return   2;\n    }\n}\n";
    private const string SpacedLaid = "class Spaced\n{\n    public static int Two()\n    {\n        return 2;\n    }\n}\n";

    /// <summary>Settings' Reformat on save: File, Save lays the changed file out as Reformat Code does before writing it, and
    /// one Undo takes the layout back; a language server that does not answer in time leaves the file saved as typed,
    /// saying why; turned off, the file is saved as typed.</summary>
    private void Reformat_on_save_lays_a_file_out_before_writing_it_as_one_step_of_undo_and_without_the_server_or_turned_off_saves_it_as_typed()
    {
        const string Laid = "using System.Text;\nusing System;\nusing System.Collections.Generic;\n\nstatic class Messy\n{\n    public static int Count()\n    {\n"
            + "        List<string> names = [];\n        Console.WriteLine(names.Count);\n        int extra = names.Count;\n        return names.Count;\n    }\n}\n";
        MainWindow window = project.Open("Messy.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        string path = project.PathOf("Messy.cs");
        window.ApplySettings(window.Settings with { ReformatOnSave = true });
        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.ToString().IndexOf("      return", StringComparison.Ordinal), 0, "        int  extra=names.Count;\n")]);
        string typed = editor.Document.Snapshot.ToString();
        Ui.Run(window, AppActions.Save);
        Ui.WaitFor(() => !document.IsModified, "Messy.cs saved");
        Assert.True(File.ReadAllText(path) == Laid, $"Save with Reformat on save on did not write the file laid out:\n{File.ReadAllText(path)}");
        Ui.Run(window, EditorActions.Undo);
        Assert.True(editor.Document.Snapshot.ToString() == typed, $"One Undo did not take the save's layout back:\n{editor.Document.Snapshot}");

        // The server held from answering: the save goes on with the text as typed once the save's limit has passed.
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = window.CSharpService!.Session!.Server.HoldNext(Passenger.Lsp.LspMethods.Formatting, _ => true, released.Task);
        Ui.Run(window, AppActions.Save, settle: false);
        Ui.PumpUntil(() => !document.IsModified, "Messy.cs saved with no answer from its language server", TimeSpan.FromMinutes(1));
        released.SetResult();
        Assert.True(held.IsCompleted, "The save did not ask the language server for its layout");
        Assert.True(File.ReadAllText(path) == typed, $"A save the server gave no layout in time did not write the file as typed:\n{File.ReadAllText(path)}");
        Assert.True(window.Status?.StartsWith("Reformat on save was not applied to Messy.cs: its language server did not answer within", StringComparison.Ordinal) == true,
            $"The status bar did not say why the file was saved as typed: {window.Status}");

        window.ApplySettings(window.Settings with { ReformatOnSave = false });
        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.ToString().IndexOf("      return", StringComparison.Ordinal), 0, "        int  more=1;\n")]);
        string kept = editor.Document.Snapshot.ToString();
        Ui.Run(window, AppActions.Save);
        Ui.WaitFor(() => !document.IsModified, "Messy.cs saved again");
        Assert.True(File.ReadAllText(path) == kept, $"Save with Reformat on save off did not write the file as typed:\n{File.ReadAllText(path)}");
        window.Close();
    }

    /// <summary>Text pasted past what Passenger reads would end the C# language server if it read it: the file says so where
    /// it goes too deep and is taken from the server, and once the paste is undone the file goes back to it, its error marked.</summary>
    private void A_paste_past_what_passenger_reads_is_said_and_kept_from_the_server_until_it_is_undone()
    {
        MainWindow window = project.Open("Separators.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Passenger.App.CSharp.CSharpService server = window.CSharpService!;
        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed");

        // The caret moved first, so the paste is a step of Undo of its own.
        Ui.PlaceAfter(editor, "\"text\";");
        window.KeyTextInput(" int deep = " + new string('(', 2_000) + "1" + new string(')', 2_000) + ";");
        Ui.WaitFor(() => document.Markers is [{ Kind: MarkerKind.Info }], "the note where the paste goes too deep");
        Assert.Equal(Languages.CSharp.CSharpParsing.TooDeep, document.Markers[0].Message);
        Assert.Equal('(', editor.Document.Snapshot[document.Markers[0].Range.Start]);
        Assert.Null(server.For(document)?.Lsp);

        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => server.For(document)?.Lsp is not null && document.Markers.Any(m => m.Kind == MarkerKind.Error), "the file back at the server, its error marked");
        Assert.DoesNotContain(document.Markers, m => m.Message == Languages.CSharp.CSharpParsing.TooDeep);
        window.Close();
    }

    private void Code_typed_below_a_comment_holding_line_breaks_only_roslyn_counts_is_squiggled_coloured_and_reformatted_in_place()
    {
        MainWindow window = project.Open("Separators.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        string before = editor.Document.Snapshot.ToString();
        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");

        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed");
        TextSnapshot text = editor.Document.Snapshot;
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("\"text\"", text.GetText(error.Range));
        Assert.Equal(5, text.GetLineFromOffset(error.Range.Start));

        // A use of the local is coloured as one only by the server, whose colours must land on the editor's line.
        int use = text.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => document.Highlighter!.GetSpans(editor.Document.Snapshot, use, 1).Any(s => s.Start == use && s.Kind == TokenKind.Local),
            "the server's colour on the local's use");

        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted", "the reformat");
        Assert.Equal(before.Replace("int total = 1;\n          return", "int total = 1; int wrong = \"text\";\n        return", StringComparison.Ordinal),
            editor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>Edits that make and unmake the line breaks only Roslyn counts (a lone '\r', a '\r' parted from its '\n'
    /// or given one, the Unicode separators, edits that meet at a break) keep the server's lines of the file in step
    /// without reading it again, so code typed below them afterwards is squiggled and coloured on its own words. So do
    /// changes that begin or end between a "\r\n"'s halves (text put between them, a line's end replaced up to its
    /// '\r', a '\n' taken with what follows it); and the server never ends over a change it cannot place.</summary>
    private void Edits_that_make_and_unmake_line_breaks_only_roslyn_counts_or_fall_between_a_crlfs_halves_keep_the_server_in_step()
    {
        string separator = ((char)0x2028).ToString();
        string paragraph = ((char)0x2029).ToString();
        string next = ((char)0x85).ToString();
        MainWindow window = project.Open("Mixed.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        List<string> ended = [];
        window.CSharpService!.Stopped += (_, why) => ended.Add(why);
        Ui.Settle();
        int At(string text) => document.Text.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        void Edit(string what, params TextEdit[] edits)
        {
            long before = Passenger.Lsp.ServerLines.CharactersRead;
            document.Text.Edit(edits, editor.Selections, editor.Selections);
            TextSnapshot text = document.Text.Snapshot;
            Passenger.Lsp.ServerLines carried = Passenger.Lsp.ServerLines.Of(text);
            long read = Passenger.Lsp.ServerLines.CharactersRead - before;
            Assert.True(read < text.Length, $"{what}: {read} characters were read to find the server's lines, the whole file again");
            Passenger.Lsp.ServerLines fresh = Passenger.Lsp.ServerLines.Of(TextSnapshot.From(text.ToString()));
            Assert.True(fresh.LineCount == carried.LineCount,
                $"{what}: the server's lines as they were carried are {carried.LineCount}, the file's are {fresh.LineCount}: every position sent from here on is off");
            for (int offset = 0; offset <= text.Length; offset++)
            {
                Assert.True(carried.PositionOf(offset) == fresh.PositionOf(offset),
                    $"{what}: offset {offset} is at {carried.PositionOf(offset)} on the server's lines as they were carried, at {fresh.PositionOf(offset)} on the file's: every position sent from here on is off");
            }
            Ui.Hold(TimeSpan.FromMilliseconds(10), "a frame between edits, as keys come");
        }

        Edit("A lone carriage return put in", new TextEdit(At("one,"), 0, "\r"));
        Edit("A line feed given to it", new TextEdit(At("one,"), 0, "\n"));
        Edit("Text put between a CR and its LF, and more further on", new TextEdit(At("\r\ntwo,") + 1, 0, "y"), new TextEdit(At("three."), 0, "x"));
        Edit("The line feed taken from after a CR", new TextEdit(At("\r\none,") + 1, 1, string.Empty));
        Edit("Separators pasted", new TextEdit(At("Pasted:"), 0, $"a{paragraph}b\r\nc\rd{separator}"));
        Edit("A separator replaced", new TextEdit(At(separator + "\rone,"), 1, " "));
        Edit("Edits meeting at a break", new TextEdit(At("xthree."), 1, "\r"), new TextEdit(At("xthree.") + 1, 0, "\n"));
        Edit("Text ending in a CR put before a LF", new TextEdit(At("y\ntwo,") + 1, 0, "end\r"));
        Edit("A CR at the end of the text", new TextEdit(document.Text.Snapshot.Length, 0, "\r"));
        Random random = new(7);
        string[] pieces = ["\r", "\n", "\r\n", separator, paragraph, next, "a", "bc"];
        for (int batch = 0; batch < 150; batch++)
        {
            int from = At("/*") + 2;
            int to = At("*/");
            int[] cuts = [.. Enumerable.Range(0, random.Next(1, 4) * 2).Select(_ => random.Next(from, to + 1)).Order()];
            TextEdit[] edits = [.. Enumerable.Range(0, cuts.Length / 2).Select(i => new TextEdit(cuts[2 * i], random.Next(3) == 0 ? cuts[2 * i + 1] - cuts[2 * i] : 0,
                string.Concat(Enumerable.Range(0, random.Next(0, 4)).Select(_ => pieces[random.Next(pieces.Length)]))))];
            Edit($"Random batch {batch + 1} ({string.Join("; ", edits.Select(e => $"{e.Start}+{e.Length} '{e.Text.Replace("\r", "\\r").Replace("\n", "\\n")}'"))})", edits);
        }
        Edit("Everything after the comment replaced", new TextEdit(At("*/") + 2, document.Text.Snapshot.Length - At("*/") - 2,
            "\n    public static int Value()\n    {\n        int total = 1;\n          return total;\n    }\n}\n"));

        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed");
        // A server that ended (a change it could not place) is started again with the whole file, which hides a drift.
        Assert.True(ended.Count == 0, $"The language server ended as the edits went to it ({string.Join("; ", ended)}): a change "
            + "reached it where its copy of the file has no such place, and only a server started again with the whole file had the file's lines");
        TextSnapshot after = editor.Document.Snapshot;
        Marker error = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("\"text\"", after.GetText(error.Range));
        int use = after.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => document.Highlighter!.GetSpans(editor.Document.Snapshot, use, 1).Any(s => s.Start == use && s.Kind == TokenKind.Local),
            "the server's colour on the local's use");

        window.OpenFile(project.PathOf("Halves.cs"));
        Ui.Settle();
        OpenDocument halves = window.EditorArea.Active!;
        Assert.Equal("Halves.cs", halves.Name);
        int Within(string text) => halves.Text.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        void Change(TextEdit edit)
        {
            halves.Text.Edit([edit], editor.Selections, editor.Selections);
            Ui.Settle();
        }

        Change(new TextEdit(Within("one,\r\n") + "one,\r".Length, 0, "x"));
        Change(new TextEdit(Within("two,\r\n"), "two,\r".Length, "deux,\r"));
        Change(new TextEdit(Within("deux,\r\n") + "deux,\r".Length, "\nthree".Length, string.Empty));
        Assert.Contains("/* one,\rx\ndeux,\r. */\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "int total = 1;");
        window.KeyTextInput(" int wrong = \"text\";");
        Ui.WaitFor(() => halves.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the error typed below the halves");
        Assert.True(ended.Count == 0, $"The language server ended as the changes went to it ({string.Join("; ", ended)}): one reached it "
            + "where its copy of the file has no such place");
        TextSnapshot changed = editor.Document.Snapshot;
        Marker[] errors = [.. halves.Markers.Where(m => m.Kind == MarkerKind.Error)];
        Assert.True(errors is [{ } only] && changed.GetText(only.Range) == "\"text\"",
            $"The errors are {string.Join("; ", errors.Select(e => $"'{changed.GetText(e.Range)}' at {e.Range.Start}: {e.Message}"))}, not one under the "
            + "string typed: the server's copy of the file is not the editor's");
        int below = changed.ToString().IndexOf("return total", StringComparison.Ordinal) + "return ".Length;
        Ui.WaitFor(() => halves.Highlighter!.GetSpans(editor.Document.Snapshot, below, 1).Any(s => s.Start == below && s.Kind == TokenKind.Local),
            "the server's colour on the local's use below the halves");
        window.Close();
    }

    /// <summary>A letter typed where Reformat Code's answer is about to delete a space, before the answer comes: the answer
    /// still lands, and deletes only the space it was made for, never the letter typed at its start.</summary>
    private void A_letter_typed_at_the_start_of_what_a_reformat_is_about_to_change_stays_once_the_reformat_lands()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result");
        window.KeyTextInput(" ");
        Ui.Settle();
        Assert.Contains("int result  = Helper.Twice(2);", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Assert.Null(window.Actions.Run(AppActions.Reformat));
        window.KeyTextInput("s");
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted" || window.Status?.Contains("not applied", StringComparison.Ordinal) == true,
            "the reformat's answer");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(window.Status == "Reformatted" && text.Contains("int result s= Helper.Twice(2);", StringComparison.Ordinal),
            $"Once Reformat Code's answer came ({window.Status}), the line is not the letter typed with the space after it deleted:\n{text}");
        window.Close();
    }

    /// <summary>Undo and Redo of a paste at a file's end, the platform's input method following the editor as a
    /// desktop's does: each changes the text and keeps its step for the other, and the language server holds the
    /// editor's text.</summary>
    private void Undo_and_redo_of_a_paste_at_a_files_end_keep_their_steps_and_the_servers_copy_with_an_input_method_following()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument program = window.EditorArea.Active!;
        Ui.WaitFor(() => program.FoldRegions.Count > 0, "the file's folds");
        Ui.WaitFor(() => window.CSharpService?.For(program)?.Lsp is not null, "the server to have the file");
        editor.Focus();
        // Asked of the focused control by Avalonia on a platform with an input method (macOS's, Windows'), as headless has none.
        Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs asked = new() { RoutedEvent = Avalonia.Input.InputElement.TextInputMethodClientRequestedEvent };
        editor.RaiseEvent(asked);
        Avalonia.Input.TextInput.TextInputMethodClient client = asked.Client ?? throw new InvalidOperationException("The editor gave no input method client");
        List<string> told = [];
        client.SurroundingTextChanged += (_, _) => told.Add(client.SurroundingText);
        client.SelectionChanged += (_, _) => told.Add($"{client.Selection.Start}:{client.Selection.End}");

        // Pasted at the end, its caret is past where the text ends once it is undone.
        string before = editor.Document.Snapshot.ToString();
        editor.Selections = SelectionSet.Single(before.Length);
        Task copied = window.Clipboard!.SetTextAsync("\nstatic class Pasted { }");
        Ui.WaitFor(() => copied.IsCompleted, "the clipboard to take the text");
        Ui.Press(window, Ui.Shortcut(EditorActions.Paste));
        string pasted = editor.Document.Snapshot.ToString();
        string pastedLine = pasted[(pasted.LastIndexOf('\n') + 1)..];
        Assert.Contains("static class Pasted { }", pastedLine, StringComparison.Ordinal);

        told.Clear();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(before, editor.Document.Snapshot.ToString());
        Assert.True(editor.Document.CanRedo, "Undo of the paste left nothing to redo: its step was lost");
        Assert.Equal(before.Length, editor.Selections.Primary.Caret);
        Assert.True(told.Contains(string.Empty), $"The input method was not told the caret's line, empty at the end, once the paste was undone: [{string.Join(" | ", told)}]");
        ServerHolds(window, program, "the paste undone");

        told.Clear();
        Ui.Run(window, EditorActions.Redo);
        Assert.Equal(pasted, editor.Document.Snapshot.ToString());
        Assert.True(editor.Document.CanUndo, "Redo of the paste left nothing to undo: its step was lost");
        Assert.True(told.Contains(pastedLine), $"The input method was not told the caret's line, the pasted one, once the paste was redone: [{string.Join(" | ", told)}]");
        ServerHolds(window, program, "the paste redone");
        window.Close();
    }

    /// <summary>Waits for the language server's copy of an open file to be the editor's text.</summary>
    private static void ServerHolds(MainWindow window, OpenDocument document, string after)
    {
        string expected = Passenger.Roslyn.Extension.TextHash.Of(document.Text.Snapshot.ToString());
        Task<IReadOnlyList<Passenger.Roslyn.Extension.TextHeld>>? asking = null;
        Ui.WaitFor(() =>
        {
            if (asking is { IsCompletedSuccessfully: true } done && done.Result is [{ Hash: { } hash }] && hash == expected)
            {
                return true;
            }
            if (asking is null || asking.IsCompleted)
            {
                asking = window.CSharpService!.For(document)!.Lsp!.HeldTextsAsync([document.Path]);
            }
            return false;
        }, $"the language server to hold the editor's text after {after}");
    }

    private void A_line_typed_while_a_rename_is_out_still_leaves_the_new_name_on_the_symbol()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.Run(window, AppActions.Rename);
        window.KeyTextInput("Double");
        Ui.Settle();
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, null);
        editor.ApplyEdits([new TextEdit(0, 0, "// typed meanwhile\n")]);
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        Assert.StartsWith("// typed meanwhile\nclass Program", text, StringComparison.Ordinal);
        Assert.Contains("int result = Helper.Double(2);", text, StringComparison.Ordinal);
        window.Close();
    }

    private void Opening_the_open_folder_again_through_mcp_keeps_the_open_files_language_server()
    {
        MainWindow window = project.Open("Program.cs");
        Passenger.App.Workspace.OpenDocument program = window.EditorArea.Active!;
        Passenger.App.CSharp.CSharpService server = window.CSharpService!;
        Passenger.App.Mcp.PassengerTools tools = new(window);

        // Agents open the workspace first, often the one already open.
        Task<Passenger.App.Mcp.Workspace> opened = tools.Open(Path.GetDirectoryName(project.PathOf("Program.cs"))!);
        Ui.WaitFor(() => opened.IsCompleted, "the open", TimeSpan.FromSeconds(10));
        Assert.Same(server, window.CSharpService);
        Assert.NotNull(server.For(program));
        Task<object> found = tools.Diagnostics("Program.cs");
        Ui.WaitFor(() => found.IsCompleted, "the diagnostics of the file open before", TimeSpan.FromSeconds(15));
        Assert.True(found.IsCompletedSuccessfully, found.Exception?.Message);
        window.Close();
    }

    /// <summary>The server counts a line at each break in Separators.cs's and Separated.cs's comments that the editor does
    /// not, so a place it names is read in the file's own text, a file not open as it is on disk: Go to Symbol's rows, Go to
    /// Declaration and Implementation, what an agent asks of the code, and the lines an agent's refactoring changed.</summary>
    private void Go_to_symbol_declaration_implementation_and_a_refactorings_changed_lines_are_the_editors_below_line_breaks_only_roslyn_counts()
    {
        MainWindow window = project.Open("Separated.cs");
        Ui.Run(window, AppActions.GoToSymbol);
        SymbolPopup popup = Assert.IsType<SymbolPopup>(window.Popup);
        window.KeyTextInput("Separated");
        Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Name == "SecondSeparated"), "the server's symbols for what was typed");
        Assert.Equal("Separated.cs:7", popup.Rows.First(r => r.Name == "FirstSeparated").Where);
        Assert.Equal("Separated.cs:12", popup.Rows.First(r => r.Name == "SecondSeparated").Where);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToSymbol);
        popup = Assert.IsType<SymbolPopup>(window.Popup);
        window.KeyTextInput("Value");
        Ui.WaitFor(() => popup.IsSettled && popup.Rows.Any(r => r.Where?.StartsWith("Separators.cs", StringComparison.Ordinal) == true), "the closed file's Value");
        Assert.Equal("Separators.cs:4", popup.Rows.First(r => r.Where?.StartsWith("Separators.cs", StringComparison.Ordinal) == true).Where);
        Ui.Press(window, "ESCAPE");

        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "=> Separators.Va");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Separators.cs", window.EditorArea.Active?.Name);
        Assert.True(editor.CaretPosition == new TextPosition(3, 22),
            $"Go to Declaration landed on line {editor.CaretPosition.Line + 1} ('{editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}'), not on Value's name on line 4");

        window.OpenFile(project.PathOf("Separated.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "    int Val");
        Ui.Run(window, AppActions.GoToImplementation);
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the two implementations to choose from");
        Assert.Equal(["Separated.cs:14", "Separated.cs:9"], window.Assist.Offered.Order(StringComparer.Ordinal));
        Assert.Contains("public int Value() => 1;", window.Assist.OfferedDetails);
        int second = window.Assist.Offered.ToList().IndexOf("Separated.cs:14");
        for (int i = 0; i < second; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("    public int |Value() => Separators.Value();", XamlTests.CaretLine(editor));

        Passenger.App.Mcp.PassengerTools tools = new(window);
        Passenger.App.Mcp.Place declared = Assert.Single(Await(tools.Definition("Separated.cs", 14, 39), "the declaration an agent asks for"));
        Assert.Equal(("Separators.cs", 4, 23, "public static int Value()"), (declared.Path, declared.Line, declared.Column, declared.Text));
        Assert.Contains("14:38 public int Value() => Separators.Value();",
            Assert.Single(Await(tools.Usages("Separators.cs", 4, 23), "the usages an agent asks for").Files, f => f.Path == "Separated.cs").Places!);
        ModelContextProtocol.Protocol.CallToolResult symbols = Await(tools.FindSymbol("FirstSeparated"), "the symbol an agent looks for");
        Assert.Contains(System.Text.Json.JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)symbols.Content[0]).Text).RootElement.EnumerateArray(),
            s => s.GetProperty("line").GetInt32() == 7);
        Assert.Contains("FirstSeparated (class, line 7)", Await(tools.Outline("Separated.cs"), "the outline an agent asks for"), StringComparison.Ordinal);

        // A parameter added to Separators.Value: its declaration on the editor's line 4, its call in Separated.cs on 14.
        System.Text.Json.JsonElement options = System.Text.Json.JsonDocument.Parse("{\"parameters\": [{\"type\": \"int\", \"name\": \"extra\", \"callValue\": \"0\"}]}").RootElement;
        Passenger.App.Mcp.RefactorAnswer changed = Await(tools.Refactor("change_signature", "Separators.cs", 4, 23, options: options), "the agent's Change Signature");
        Assert.Equal(["Separated.cs changed on 14", "Separators.cs changed on 4"],
            changed.Files.Select(f => $"{f.Path} {f.Change} on {string.Join(", ", f.Lines)}").Order(StringComparer.Ordinal));
        Assert.Contains("public int Value() => Separators.Value(0);", File.ReadAllText(project.PathOf("Separated.cs")), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Go to Declaration opens the other file at the method, and Forward after Back goes there again when the caret
    /// moved in between; Back lands on the call it left after a line is added above it; and Go to Declaration on a library
    /// type opens its decompiled source, read-only.</summary>
    private void Go_to_declaration_back_and_forward_land_where_the_code_is_and_a_library_type_opens_its_decompiled_source_read_only()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Helper.Tw");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.Settle();
        Assert.Equal("Helper.cs", window.EditorArea.Active?.Name);
        Assert.Equal(2, editor.CaretPosition.Line);
        TextPosition definition = editor.CaretPosition;

        Ui.Press(window, Ui.Shortcut(AppActions.Back));
        Assert.Equal("Program.cs", window.EditorArea.Active?.Name);
        Ui.Press(window, "RIGHT");
        Ui.Press(window, "DOWN");
        Ui.Press(window, Ui.Shortcut(AppActions.Forward));

        Assert.True(window.EditorArea.Active?.Name == "Helper.cs", "a caret moved after Back lost Forward's way to the definition");
        Assert.Equal(definition, editor.CaretPosition);

        window.OpenFile(project.PathOf("Area.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "=> O");
        TextPosition call = editor.CaretPosition;
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.Settle();
        Assert.True(editor.CaretPosition.Line < call.Line, "go to declaration never reached Of, above its call");

        Ui.Press(window, "END");
        Ui.Press(window, "ENTER");
        Ui.Press(window, Ui.Shortcut(AppActions.Back));

        Assert.Equal("    public static int Room() => Of(3, 4);", editor.Document.Snapshot.GetLineText(call.Line + 1));
        Assert.True(new TextPosition(call.Line + 1, call.Column) == editor.CaretPosition,
            $"Back landed at {editor.CaretPosition}, not on the call it left, which a line added above moved to {new TextPosition(call.Line + 1, call.Column)}");

        window.OpenFile(project.PathOf("Program.cs"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument decompiled = Decompiled(window, "        in");
        Assert.Equal("Int32.cs", decompiled.Name);
        // Off Windows, in the server's own temporary folder, among those of the person's own.
        if (Passenger.Lsp.RoslynServerFolder.Temporaries(Passenger.App.CSharp.CSharpService.ServerCache) is { } own)
        {
            Assert.True(PathNames.IsUnder(decompiled.Path, PathNames.Canonical(own)), $"The language server wrote {decompiled.Path}, outside the temporary folders of the person's own its servers are given, {own}");
        }
        Assert.Contains("struct Int32", decompiled.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("struct Int32", decompiled.Text.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);

        string before = decompiled.Text.Snapshot.ToString();
        window.KeyTextInput("x");
        Ui.Settle();
        Assert.Equal(before, decompiled.Text.Snapshot.ToString());
        Assert.True(editor.IsReadOnly);
        window.Close();
    }

    /// <summary>On a library type's decompiled source, every action of the Code and Refactor menus that would change the file
    /// greys saying it is read-only (those of another language's files saying that instead), and those that only fold it
    /// or act on the language server stay enabled, as do Go to Declaration and Find Usages, which only read it; Code
    /// Cleanup's and Rename's keys there say so and change nothing.</summary>
    private void Every_action_of_the_code_and_refactor_menus_that_would_change_a_decompiled_file_greys_saying_it_is_read_only()
    {
        MainWindow window = project.Open("Program.cs");
        Passenger.App.Workspace.OpenDocument decompiled = Decompiled(window, "        in");
        string before = decompiled.Text.Snapshot.ToString();
        string readOnly = $"{decompiled.Name} is read-only";
        HashSet<string> reading = [EditorActions.Unfold, EditorActions.UnfoldAll, EditorActions.Fold, EditorActions.FoldToDefinitions, EditorActions.FoldAll,
            EditorActions.UnfoldDocComments, EditorActions.FoldDocComments, EditorActions.ToggleFold, AppActions.ToggleSolutionAnalysis, AppActions.RestartCSharpServer];
        HashSet<string> otherLanguage = [AppActions.OrderScriptParts, AppActions.ExtractResource, AppActions.ExtractStyle];

        ActionContext context = window.Actions.Context();
        List<string> offered = [];
        List<string> greyed = [];
        foreach (string id in MainMenu.Build(window.Actions.All).Where(m => m.Title is "Code" or "Refactor").SelectMany(ActionsIn))
        {
            string? why = window.Actions.WhyNot(id, context);
            if (reading.Contains(id) ? why is not null : otherLanguage.Contains(id) ? why is null : why != readOnly)
            {
                (reading.Contains(id) ? greyed : offered).Add($"{id} ({why ?? "enabled"})");
            }
        }
        foreach (string id in new[] { AppActions.GoToDeclaration, AppActions.FindUsages })
        {
            if (window.Actions.WhyNot(id, context) is { } why)
            {
                greyed.Add($"{id} ({why})");
            }
        }
        Assert.True(offered.Count == 0, $"On the decompiled {decompiled.Name}, which nothing can change, these are offered or greyed for another reason: {string.Join(", ", offered)}");
        Assert.True(greyed.Count == 0, $"On the decompiled {decompiled.Name}, these only read it but are greyed: {string.Join(", ", greyed)}");

        LiveText status = window.GetVisualDescendants().OfType<LiveText>().Single(t => t.Name == "StatusText");
        foreach (string id in new[] { AppActions.CodeCleanup, AppActions.Rename })
        {
            // Each key's own word, never the one before it.
            status.Text = null;
            Ui.Run(window, id);
            Assert.True(window.Status == readOnly, $"{id}'s keys on the decompiled {decompiled.Name} said {window.Status}, not that it is read-only");
            Assert.Equal(before, decompiled.Text.Snapshot.ToString());
        }
        window.Close();

        static IEnumerable<string> ActionsIn(MenuEntry menu) => menu.Action is { } id ? [id] : (menu.Items ?? []).SelectMany(ActionsIn);
    }

    /// <summary>A library type's source the language server decompiled for one window stays where it was written, and Go
    /// to Declaration there opens it again, after another window's server, a program of its own, decompiles one too.</summary>
    private void A_library_types_source_decompiled_for_one_window_stays_as_another_windows_server_decompiles_one()
    {
        MainWindow window = project.Open("Program.cs");
        string written = Decompiled(window, "        in").Path;
        string other = TestFolders.Create("assist-beside-");
        File.WriteAllText(Path.Combine(other, "Beside.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(other, "Program.cs"), "class Program\n{\n    static void Main()\n    {\n        string text = \"\";\n    }\n}\n");
        Restore.Run(other);
        MainWindow beside = Ui.OpenFolder(other);
        try
        {
            beside.OpenFile("Program.cs");
            Ui.WaitFor(() => beside.CSharpService is { IsReady: true }, () => $"the other window's language server to load its project ({beside.CSharpService?.State})", TimeSpan.FromMinutes(3));
            string besides = Decompiled(beside, "        str").Path;
            Assert.True(File.Exists(written), $"{written}, the source of Int32 the first window's server decompiled, was deleted as the other window's server decompiled {besides}");
        }
        finally
        {
            beside.Close();
        }
        Passenger.App.Workspace.OpenDocument shown = window.EditorArea.Active!;
        Assert.True(window.EditorArea.CloseTab(window.EditorArea.Groups.First(g => g.Has(shown)), shown), $"{shown.Name}'s tab did not close");
        window.OpenFile(project.PathOf("Program.cs"));
        Ui.Settle();
        Assert.Equal(written, Decompiled(window, "        in").Path);
        window.Close();
    }

    /// <summary>Go to Declaration from the first keyword after <paramref name="before"/> in the file shown, a library type
    /// the language server decompiles, and the file it opens once the editor shows it.</summary>
    private static Passenger.App.Workspace.OpenDocument Decompiled(MainWindow window, string before)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, before);
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.IsDecompiled == true,
            () => $"the decompiled source to open: the window shows {window.EditorArea.Active?.Path}, saying {window.Status}, C# {window.CSharpService?.State}{Ui.IdeLogTail()}{Ui.ServerLogTail()}");
        return window.EditorArea.Active!;
    }

    private void Code_cleanup_removes_unused_usings_sorts_the_rest_and_reformats()
    {
        MainWindow window = project.Open("Messy.cs");
        Passenger.App.Workspace.OpenDocument messy = window.EditorArea.Active!;
        Ui.WaitFor(() => window.CSharpService!.For(messy)!.Diagnostics.Any(d => d.Code?.ToString() == "IDE0005"), "the unused using to be found");
        Ui.Run(window, AppActions.CodeCleanup);
        Ui.WaitFor(() => window.Status?.StartsWith("Code cleanup", StringComparison.Ordinal) == true, "the cleanup to finish");
        string text = messy.Text.Snapshot.ToString();
        Assert.StartsWith("using System;\nusing System.Collections.Generic;\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text", text, StringComparison.Ordinal);
        Assert.Contains("\n        List<string> names = [];\n", text, StringComparison.Ordinal);
        Assert.Contains("\n        return names.Count;\n", text, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>Refactor This lists only the refactorings at the selection, the server's Extract method as Passenger's own
    /// Extract Method; Extract Method asks for each new method's name at
    /// once, its rename box at the call offering the method just made, not the one before; and lines selected down to the
    /// next statement leave that statement on its own line.</summary>
    private void Extract_method_asks_each_new_methods_name_and_keeps_the_next_statement_on_its_line_and_refactor_this_lists_only_what_fits()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string text = editor.Document.Snapshot.ToString();
        int from = text.IndexOf("for (", StringComparison.Ordinal);
        int to = text.IndexOf("        }\n        return", StringComparison.Ordinal) + "        }".Length;
        editor.Selections = SelectionSet.Single(new Selection(from, to));
        Ui.Run(window, AppActions.RefactorThis);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the refactorings");
        IReadOnlyList<string> offered = window.Assist.Offered;
        Assert.True(offered.Contains("Extract Method…") && !offered.Contains("Extract method"),
            $"Refactor This lists the server's Extract method other than as Extract Method…, which names the method it makes: {string.Join("; ", offered)}");
        Assert.DoesNotContain(offered, t => t.StartsWith("Suppress", StringComparison.Ordinal));
        Ui.Press(window, "ESCAPE");

        editor.Selections = SelectionSet.Single(new Selection(from, to));
        int version = editor.Document.Version;
        Ui.Run(window, AppActions.ExtractMethod);
        Ui.WaitFor(() => editor.Document.Version != version, "the loop to be extracted");
        Ui.Settle();
        string first = window.Assist.Renaming ?? throw new Xunit.Sdk.XunitException("No rename was offered for the method the loop became");
        string extracted = editor.Document.Snapshot.ToString();
        Assert.True(extracted.Contains($"total = {first}(total);", StringComparison.Ordinal), $"The rename offered {first}, not the method the loop became:\n{extracted}");
        // The suggested name is kept, so the method extracted next needs another.
        Ui.Press(window, "ESCAPE");

        int product = extracted.IndexOf("i * 2", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(product, product + "i * 2".Length));
        version = editor.Document.Version;
        Ui.Run(window, AppActions.ExtractMethod);
        Ui.WaitFor(() => editor.Document.Version != version, "the product to be extracted");
        Ui.Settle();
        string second = window.Assist.Renaming ?? throw new Xunit.Sdk.XunitException("No rename was offered for the method the product became");
        Assert.True(editor.Document.Snapshot.ToString().Contains($"total += {second}(i);", StringComparison.Ordinal),
            $"The rename offered {second}, not the method just made:\n{editor.Document.Snapshot}");
        window.KeyTextInput("Double");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("Double(", StringComparison.Ordinal), "the second method to take its name");
        string named = editor.Document.Snapshot.ToString();
        Assert.Contains("total += Double(i);", named, StringComparison.Ordinal);
        Assert.Contains($"total = {first}(total);", named, StringComparison.Ordinal);
        Assert.DoesNotContain(second, named, StringComparison.Ordinal);
        Assert.Matches(@"static \w+ Double\(int i\)", named);

        window.OpenFile(project.PathOf("Average.cs"));
        Ui.Settle();
        editor.GoTo(new TextPosition(8, 8));
        for (int i = 0; i < 5; i++)
        {
            Ui.Press(window, "shift Down");
        }
        version = editor.Document.Version;
        Ui.Run(window, AppActions.ExtractMethod);
        Ui.WaitFor(() => editor.Document.Version != version, "the average's loop to be extracted");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        string after = editor.Document.Snapshot.ToString();
        Assert.True(after.Contains("        return total / count;\n", StringComparison.Ordinal), $"The return after the loop was joined to the call that replaced it:\n{after}");
        window.Close();
    }

    /// <summary>Choosing an override from completion writes the overriding method with the caret in its body, and what a
    /// completion or a fix writes takes the file's line breaks, whichever the server writes.</summary>
    private void What_a_completion_or_a_fix_writes_takes_the_files_line_breaks_whichever_the_server_writes()
    {
        string lfFile = File.ReadAllText(project.PathOf("Breaks/LfFile.cs"));
        try
        {
            MainWindow window = project.Open("Breaks/CrlfFile.cs");
            CodeEditor editor = window.EditorArea.CodeEditor;
            OverrideToString(window, editor);
            AssertBreaks(editor.Document.Snapshot.ToString(), "\r\n", "    }\r\n\r\n    static void Make()", "the override in the CRLF file");
            OpenDocument crlf = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "int made = 0;");
            window.KeyTextInput(" int twice = LfFile.Twice(made);");
            Ui.WaitFor(() => window.CSharpService!.For(crlf)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0117"), "Twice's error");
            Ui.PlaceAfter(editor, "LfFile.Tw");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Generate method 'Twice'"), "the offer to generate Twice");
            Choose(window, "Generate method 'Twice'");
            Ui.WaitFor(() => File.ReadAllText(project.PathOf("Breaks/LfFile.cs")).Contains("Twice", StringComparison.Ordinal), "Twice to be made in LfFile.cs");
            AssertBreaks(File.ReadAllText(project.PathOf("Breaks/LfFile.cs")), "\n", "Twice(int made)\n    {\n", "the method made in the LF file on disk");
            window.Close();

            File.WriteAllText(project.PathOf("Breaks/LfFile.cs"), lfFile);
            window = project.Open("Breaks/LfFile.cs");
            editor = window.EditorArea.CodeEditor;
            OverrideToString(window, editor);
            string overridden = editor.Document.Snapshot.ToString();
            Assert.True(overridden.Contains("    public int X { get; }\n    public override string ToString()\n    {\n        return base.ToString();\n    }\n", StringComparison.Ordinal),
                $"Choosing ToString() did not write the overriding method:\n{overridden}");
            AssertBreaks(overridden, "\n", "    }\n\n    static void Make()", "the override in the LF file");
            OpenDocument lf = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "int made = 0;");
            window.KeyTextInput(" Foo gadget = null;");
            Ui.WaitFor(() => window.CSharpService!.For(lf)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
            Ui.PlaceAfter(editor, "Fo");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Generate nested class 'Foo'"), "the offer to nest Foo");
            Choose(window, "Generate nested class 'Foo'");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("class Foo", StringComparison.Ordinal), "Foo to be generated");
            AssertBreaks(editor.Document.Snapshot.ToString(), "\n", "    }\n\n    private class Foo\n    {\n    }\n}\n", "the nested type in the LF file");
            window.Close();
        }
        finally
        {
            File.WriteAllText(project.PathOf("Breaks/LfFile.cs"), lfFile);
        }

        static void OverrideToString(MainWindow window, CodeEditor editor)
        {
            Ui.PlaceAfter(editor, "public int X { get; }");
            editor.Focus();
            Ui.Press(window, "ENTER");
            window.KeyTextInput("override ");
            window.KeyTextInput("T");
            Ui.Settle();
            window.KeyTextInput("oS");
            Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "ToString()", "ToString offered to override");
            Ui.Press(window, "ENTER");
            Ui.Settle();
            string text = editor.Document.Snapshot.ToString();
            Assert.True(text.IndexOf("return base.ToString();", StringComparison.Ordinal) + "return base.ToString();".Length == editor.Selections.Primary.Caret,
                $"The caret is at {editor.CaretPosition}, not after the call to the base method in the new body");
        }

        // The text with its line breaks shown, and whether each is the file's own.
        static void AssertBreaks(string text, string eol, string written, string what)
        {
            string shown = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n\n", StringComparison.Ordinal);
            string name = eol == "\n" ? "LF" : "CRLF";
            Assert.True(text.Contains(written, StringComparison.Ordinal), $"{what} is not written with the file's {name} line breaks:\n{shown}");
            bool mixed = eol == "\n" ? text.Contains('\r', StringComparison.Ordinal) : text.Replace("\r\n", "", StringComparison.Ordinal).AsSpan().IndexOfAny('\r', '\n') >= 0;
            Assert.False(mixed, $"After {what} the {name} file has another line break among its own:\n{shown}");
        }
    }

    private void A_tab_indented_editorconfig_has_enter_tab_a_template_a_completion_and_a_fix_indent_with_tabs()
    {
        MainWindow window = project.Open("Tabs/Tabbed.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("Tab, from .editorconfig", IndentShown(window));

        Ui.PlaceAfter(editor, "public int X { get; }");
        editor.Focus();
        Ui.Press(window, "ENTER");
        Ui.Press(window, "TAB");
        window.KeyTextInput("// note");
        Assert.Contains("\tpublic int X { get; }\n\t\t// note\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.Press(window, "shift TAB");
        Assert.Contains("\tpublic int X { get; }\n\t// note\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        OverrideAfter(window, editor, "// note");
        AssertTabbed(editor, "\t// note\n\tpublic override string ToString()\n\t{\n\t\treturn base.ToString();\n\t}\n", "the override");

        Ui.PlaceAfter(editor, "int made = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("if");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Ui.Press(window, "ESCAPE");
        AssertTabbed(editor, "\t\tint made = 0;\n\t\tif (true)\n\t\t{\n\t\t\t\n\t\t}\n", "the if template");

        OpenDocument tabbed = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "int made = 0;");
        window.KeyTextInput(" Foo gadget = null;");
        Ui.WaitFor(() => window.CSharpService!.For(tabbed)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
        Ui.PlaceAfter(editor, "Fo");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate nested class 'Foo'"), "the offer to nest Foo");
        Choose(window, "Generate nested class 'Foo'");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("class Foo", StringComparison.Ordinal), "Foo to be generated");
        AssertTabbed(editor, "\t}\n\n\tprivate class Foo\n\t{\n\t}\n}\n", "the nested type");
        window.Close();
    }

    private void An_editorconfig_marks_what_nothing_reads_says_what_a_key_does_and_completes_roslyns_options_and_their_values()
    {
        MainWindow window = project.Open("Keys/.editorconfig");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument keys = window.EditorArea.Active!;
        string[] marked = ["ZZ9999 = No analyzer Sample runs reports ZZ9999", "csharp_unread_option = Nothing reads csharp_unread_option: no option of Roslyn's is named so",
            "sometimes = Roslyn does not read sometimes as csharp_space_after_cast: it takes true or false", "lfx = end_of_line takes lf, crlf or cr, not lfx"];
        Ui.WaitFor(() => Marked().SequenceEqual(marked), () => $"The .editorconfig's marks are {string.Join("; ", Marked())}");

        editor.Focus();
        int key = editor.Document.Snapshot.ToString().IndexOf("csharp_style_var_elsewhere", StringComparison.Ordinal) + 1;
        window.MouseMove(editor.TranslatePoint(editor.PointOf(key) + new Point(1, editor.LineHeight / 2), window)!.Value, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the key's hover card");
        string card = window.Assist.HoverText!;
        Assert.True(card.Contains("var preferences", StringComparison.Ordinal) && card.Contains("Takes true or false", StringComparison.Ordinal)
            && card.Contains("Default: ", StringComparison.Ordinal), $"The card on csharp_style_var_elsewhere does not say its group, values and default: {card}");
        window.MouseMove(new Point(1, 1), Avalonia.Input.RawInputModifiers.None);

        Ui.PlaceAfter(editor, "end_of_line = lfx");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("csharp_style_var_wh");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "csharp_style_var_when_type_is_apparent",
            () => $"Completion lists {string.Join(", ", window.Assist.CompletionLabels.Take(5))} for csharp_style_var_wh");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.SequenceEqual(["true", "false"]), () => $"The key chosen lists {string.Join(", ", window.Assist.CompletionLabels)} for its value");
        Ui.Press(window, "ENTER");
        Assert.EndsWith("\ncsharp_style_var_when_type_is_apparent = true\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();

        IEnumerable<string> Marked() => keys.Markers.Select(m => $"{keys.Text.Snapshot.GetText(m.Range.Start, m.Range.Length)} = {m.Message}");
    }

    private void Where_no_editorconfig_says_completions_and_reformatting_indent_as_the_files_own_lines()
    {
        MainWindow window = project.Open("Detected/TwoSpaces.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("2 spaces, detected", IndentShown(window));
        OverrideAfter(window, editor, "public int X { get; }");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.Contains("  public int X { get; }\n  public override string ToString()\n  {\n    return base.ToString();\n  }\n", StringComparison.Ordinal),
            $"The override in a file of two spaces is not indented with two:\n{text}");
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted", "the reformat");
        Assert.True(editor.Document.Snapshot.ToString().Contains("\n    if (made > 0)\n    {\n      made--;\n", StringComparison.Ordinal),
            $"Reformatting a file of two spaces indented it otherwise:\n{editor.Document.Snapshot}");

        window.OpenFile(project.PathOf("Detected/TabsOwn.cs"));
        Ui.Settle();
        Assert.Equal("Tab, detected", IndentShown(window));
        OverrideAfter(window, editor, "public int X { get; }");
        AssertTabbed(editor, "\tpublic int X { get; }\n\tpublic override string ToString()\n\t{\n\t\treturn base.ToString();\n\t}\n", "the override in a file of tabs");
        window.Close();
    }

    /// <summary>A space typed after override in a class lists the members it may override, with no live template among
    /// them, and Enter writes the one chosen as an override calling the base's.</summary>
    private void A_space_after_override_lists_the_members_to_override_and_no_template_and_Enter_writes_one()
    {
        MainWindow window = project.Open("Figures.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "public double Radius { get; set; }");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("override ");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Perimeter()"), () => $"the members to override after a space, where the list shows [{string.Join(", ", window.Assist.CompletionLabels)}]");
        IReadOnlyList<string> labels = window.Assist.CompletionLabels;
        Assert.True(labels.Contains("Describe(string prefix)") && labels.Contains("ToString()"), $"After override the list shows [{string.Join(", ", labels)}], not Figure's and object's members");
        string[] templates = [.. labels.Where(l => l is "ctor" or "prop" or "propg" or "svm" or "sim")];
        Assert.True(templates.Length == 0, $"After override the list offers the live templates [{string.Join(", ", templates)}], which write a member over the modifier");
        window.KeyTextInput("Per");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "Perimeter()", "Perimeter offered first once Per is typed");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("base.Perimeter()", StringComparison.Ordinal), () => $"the override written; the file reads:\n{editor.Document.Snapshot}");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.Contains("public override double Perimeter()", StringComparison.Ordinal) && !text.Contains("override public", StringComparison.Ordinal),
            $"Enter after override wrote:\n{text}");
        window.Close();
    }

    /// <summary>Chooses ToString() from completion on a new line after some text, which overrides it there.</summary>
    private static void OverrideAfter(MainWindow window, CodeEditor editor, string after)
    {
        Ui.PlaceAfter(editor, after);
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("override ");
        window.KeyTextInput("T");
        Ui.Settle();
        window.KeyTextInput("oS");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "ToString()", "ToString offered to override");
        Ui.Press(window, "ENTER");
        Ui.Settle();
    }

    /// <summary>That the text holds what was written, and that no line of it is indented with a space.</summary>
    private static void AssertTabbed(CodeEditor editor, string written, string what)
    {
        string text = editor.Document.Snapshot.ToString();
        string shown = text.Replace("\t", "→", StringComparison.Ordinal);
        Assert.True(text.Contains(written, StringComparison.Ordinal), $"{what} is not indented with tabs:\n{shown}");
        Assert.False(text.Split('\n').Any(line => line.StartsWith(' ')), $"After {what} a line of the tab-indented file starts with a space:\n{shown}");
    }

    private static string? IndentShown(MainWindow window) => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(window, "IndentText")!.Text;

    private void Choosing_await_in_a_method_not_yet_async_makes_the_method_async_as_well()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("a");
        Ui.Settle();
        window.KeyTextInput("wa");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "await", "await offered");

        Ui.Press(window, "ENTER");
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        Assert.Matches(@"public static async [\w.<>]+ Sum\(\)\n    \{\n        int total = 0;\n        await\n", text);
        Assert.True(text.IndexOf("await", StringComparison.Ordinal) + "await".Length == editor.Selections.Primary.Caret,
            $"The caret is at {editor.CaretPosition}, not after the await:\n{text}");
        window.Close();
    }

    private void A_fix_with_choices_offers_them_as_a_second_list_and_makes_the_one_chosen()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument program = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "Helper.Twice(2);");
        window.KeyTextInput(" Foo gadget = null;");
        Ui.WaitFor(() => window.CSharpService!.For(program)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
        Ui.PlaceAfter(editor, "Fo");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate type 'Foo'"), "the offer to generate Foo");
        Choose(window, "Generate type 'Foo'");

        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate class 'Foo'"), "the choices Generate type offers");
        Assert.Contains("Generate nested class 'Foo'", window.Assist.Offered);
        Choose(window, "Generate class 'Foo'");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("class Foo", StringComparison.Ordinal), "Foo to be generated");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.Contains("}\n\ninternal class Foo\n{\n}", StringComparison.Ordinal), $"Foo was not generated beside Program:\n{text}");
        window.Close();
    }

    private void A_fix_the_language_server_will_not_make_once_chosen_says_why_in_the_status_bar()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument program = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "Helper.Twice(2);");
        window.KeyTextInput(" int more = Helper.Thrice(2);");
        Ui.WaitFor(() => window.CSharpService!.For(program)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0117"), "Thrice's error");
        Ui.PlaceAfter(editor, "Helper.Thr");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Generate method 'Thrice'"), "the offer to generate Thrice");

        // The call is changed under the open list, so the server no longer has the fix it offered when it is chosen.
        int at = editor.Document.Snapshot.ToString().IndexOf("Thrice", StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(at, "Thrice".Length, "Twice")]);
        Choose(window, "Generate method 'Thrice'");
        Ui.WaitFor(() => window.Status?.StartsWith("Generate method 'Thrice' was not applied", StringComparison.Ordinal) == true,
            $"the status bar to say the fix was not made (it says {window.Status})");
        Assert.StartsWith("Generate method 'Thrice' was not applied: the language server refused it: ", window.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("Thrice(", File.ReadAllText(project.PathOf("Helper.cs")), StringComparison.Ordinal);
        window.Close();
    }

    private void Fix_all_offers_its_scopes_and_fixes_every_occurrence_in_the_one_chosen()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument loop = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "int total = 0;");
        window.KeyTextInput(" int one = (int)1; int two = (int)2;");
        Ui.WaitFor(() => window.CSharpService!.For(loop)!.Diagnostics.Count(d => d.Code?.ToString() == "IDE0004") == 2, "both casts found redundant");
        Ui.PlaceAfter(editor, "(in");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Fix All: Remove Unnecessary Cast"), "the offer to remove every such cast");
        Choose(window, "Fix All: Remove Unnecessary Cast");

        Ui.WaitFor(() => window.Assist.Offered.Contains("Document"), "the scopes Fix All may cover");
        Assert.Contains("Project", window.Assist.Offered);
        Assert.Contains("Solution", window.Assist.Offered);
        Choose(window, "Document");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("int one = 1; int two = 2;", StringComparison.Ordinal), "both casts removed");
        window.Close();
    }

    private void Fix_all_in_the_solution_reaching_a_file_not_open_is_undone_in_every_file_it_changed_at_once()
    {
        string closed = project.PathOf("Casts.cs");
        string casts = File.ReadAllText(closed);
        MainWindow window = project.Open("Loop.cs");
        List<string> asked = [];
        window.Confirm = (title, _, _) =>
        {
            asked.Add(title);
            return Task.FromResult(true);
        };
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument loop = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "int total = 0;");
        window.KeyTextInput(" int one = (int)1;");
        string typed = editor.Document.Snapshot.ToString();
        Ui.WaitFor(() => window.CSharpService!.For(loop)!.Diagnostics.Any(d => d.Code?.ToString() == "IDE0004"), "the cast found redundant");
        Ui.PlaceAfter(editor, "(in");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains("Fix All: Remove Unnecessary Cast"), "the offer to remove every such cast");
        Choose(window, "Fix All: Remove Unnecessary Cast");
        Ui.WaitFor(() => window.Assist.Offered.Contains("Solution"), "the scopes Fix All may cover");
        Choose(window, "Solution");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("int one = 1;", StringComparison.Ordinal)
            && File.ReadAllText(closed).Contains("=> 3;", StringComparison.Ordinal), "the cast removed in the open file and in the one not open");

        editor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(["Undo"], asked);
        Assert.Equal(typed, editor.Document.Snapshot.ToString());
        string after = File.ReadAllText(closed);
        Assert.True(after == casts, $"Undo left the Fix All's change in Casts.cs, which was not open, with no way back but Local History:\n{after}");
        window.Close();
    }

    private void Through_mcp_an_action_with_choices_lists_them_asks_for_one_and_applies_the_one_named()
    {
        string source = File.ReadAllText(project.PathOf("Program.cs"));
        MainWindow window = project.Open("Program.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument program = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "Helper.Twice(2);");
            window.KeyTextInput(" Foo gadget = null;");
            Ui.WaitFor(() => window.CSharpService!.For(program)!.Diagnostics.Any(d => d.Code?.ToString() == "CS0246"), "Foo's error");
            Passenger.App.Mcp.PassengerTools tools = new(window);
            int column = editor.Document.Snapshot.GetLineText(4).IndexOf("Foo", StringComparison.Ordinal) + 2;

            IReadOnlyList<string> offered = Await(tools.CodeActions("Program.cs", 5, column), "the actions");
            Assert.Contains("Generate type 'Foo' [quickfix], choices: Generate class 'Foo'; Generate nested class 'Foo'", offered);
            Task<string> unnamed = tools.ApplyCodeAction("Program.cs", 5, column, "Generate type 'Foo'");
            Ui.WaitFor(() => unnamed.IsCompleted, "the refusal");
            Assert.Contains("Generate nested class 'Foo'", Assert.IsType<ModelContextProtocol.McpException>(unnamed.Exception?.InnerException).Message, StringComparison.Ordinal);

            string applied = Await(tools.ApplyCodeAction("Program.cs", 5, column, "Generate type 'Foo'", choice: "Generate nested class 'Foo'"), "the action");
            Assert.Equal("Applied 'Generate type 'Foo'' (Generate nested class 'Foo') to 1 file", applied);
            string saved = File.ReadAllText(project.PathOf("Program.cs"));
            Assert.True(saved.Contains("    private class Foo\n    {\n    }\n}", StringComparison.Ordinal), $"Foo was not nested in Program, and saved:\n{saved}");
        }
        finally
        {
            window.Close();
            File.WriteAllText(project.PathOf("Program.cs"), source);
        }
    }

    /// <summary>Escape closes the code's lists of choices and its rename box wherever in the window the keys are: Show
    /// Context Actions' list and the rename box once Find Action, closed, has given them back the keys they had, and
    /// Generate's once the Explorer has them, which keeps them; Generate's list and the rename box close on a click away
    /// too, renaming nothing.</summary>
    private void Escape_closes_the_lists_of_choices_and_the_rename_box_wherever_the_keys_are()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int res");
        editor.Focus();
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "Show Context Actions' list for result");
        Ui.Run(window, AppActions.FindAction);
        Assert.NotNull(window.Popup);
        Ui.Press(window, "ESCAPE");
        Assert.True(window.Popup is null && window.Assist.Offered.Count > 0 && OfferedRow(window, window.Assist.Offered[0]).IsFocused,
            $"Find Action closed did not give Show Context Actions' list back the keys on its chosen row (Find Action {(window.Popup is null ? "closed" : "open")}, "
            + $"the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, the list offering {window.Assist.Offered.Count})");
        Ui.Press(window, "ESCAPE");
        Assert.True(window.Assist.Offered.Count == 0,
            $"Escape with the keys back in Show Context Actions' list left it open: [{string.Join("; ", window.Assist.Offered)}]");

        window.OpenFile(project.PathOf("Point.cs"));
        Ui.Settle();
        string point = editor.Document.Snapshot.ToString();
        Ui.PlaceAfter(editor, "public int X");
        Ui.Run(window, AppActions.Generate);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "what can be generated");
        Ui.Run(window, AppActions.FocusExplorer);
        Assert.True(window.ExplorerView.Rows().IsKeyboardFocusWithin && window.Assist.Offered.Count > 0,
            $"Focus Explorer left the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"} and Generate's list offering {window.Assist.Offered.Count}");
        Ui.Press(window, "ESCAPE");
        Assert.True(window.Assist.Offered.Count == 0, $"Escape in the Explorer left Generate's list open: [{string.Join("; ", window.Assist.Offered)}]");
        Assert.True(window.ExplorerView.Rows().IsKeyboardFocusWithin,
            $"Escape that closed Generate's list took the Explorer's keys to {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}");

        editor.Focus();
        Ui.Run(window, AppActions.Generate);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "what can be generated, again");
        Ui.Click(editor, editor.PointOf(0) + new Point(1, editor.LineHeight / 2));
        Assert.True(window.Assist.Offered.Count == 0, $"A click in the editor away from Generate's list left it open: [{string.Join("; ", window.Assist.Offered)}]");

        Ui.PlaceAfter(editor, "public int Y");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Y", "the rename box");
        window.KeyTextInput("Height");
        Ui.Click(editor, editor.PointOf(0) + new Point(1, editor.LineHeight / 2));
        Assert.True(window.Assist.Renaming is null, $"A click in the editor away from the rename box left it open, holding {window.Assist.Renaming}");
        Ui.PlaceAfter(editor, "public int Y");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Y", "the rename box again");
        Ui.Run(window, AppActions.FindAction);
        Ui.Press(window, "ESCAPE");
        Assert.True(window.FocusManager?.GetFocusedElement() is TextBox && window.Assist.Renaming == "Y",
            $"Find Action closed did not give the rename box back the keys (the keys with {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, the box holding {window.Assist.Renaming ?? "nothing"})");
        Ui.Press(window, "ESCAPE");
        Assert.True(window.Assist.Renaming is null, $"Escape with the keys back in the rename box left it open, holding {window.Assist.Renaming}");
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString() == point, $"The rename box closed by a click away or Escape renamed Y:\n{editor.Document.Snapshot}");
        window.Close();
    }

    /// <summary>The row of the open list of choices that shows these words.</summary>
    private static ListBoxItem OfferedRow(MainWindow window, string title) =>
        window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(row => row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == title))
        ?? throw new Xunit.Sdk.XunitException($"No row of the list of choices shows {title}");

    /// <summary>Where the editor's layer of popups lies in its window.</summary>
    internal static Rect Layer(MainWindow window)
    {
        Canvas assist = window.EditorArea.AssistLayer;
        return new Rect(assist.TranslatePoint(default, window) ?? default, assist.Bounds.Size);
    }

    /// <summary>A card shown in the editor whole: inside its layer, every block of its words laid out across inside the card
    /// (a list of rows in it scrolls up and down); its blocks of words, in order.</summary>
    internal static IReadOnlyList<TextBlock> WholeInEditor(MainWindow window, Control card, string what)
    {
        Rect layer = Layer(window);
        Rect at = new(card.TranslatePoint(default, window) ?? default, card.Bounds.Size);
        Assert.True(layer.Inflate(0.5).Contains(at), $"{what} runs past the editor's edge, cut there: it lies at {at} in the editor at {layer}");
        List<TextBlock> words = [.. card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && Said(t).Length > 0)];
        foreach (TextBlock said in words)
        {
            Rect lies = new(said.TranslatePoint(default, window) ?? default, said.Bounds.Size);
            Assert.True(lies.X >= at.X - 0.5 && lies.Right <= at.Right + 0.5 && said.TextLayout.Width <= said.Bounds.Width + 0.5,
                $"'{Said(said)}' runs past {what} at {at}: it lies at {lies}, {said.TextLayout.Width:0.#} wide");
        }
        return words;
    }

    /// <summary>A block's words: its text, or its runs' where it is drawn in runs (a card's code).</summary>
    private static string Said(TextBlock block) =>
        block.Text is { Length: > 0 } text ? text : string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []);

    private void Parameter_info_keeps_a_sentence_apart_from_a_parameter_its_documentation_names_straight_after_it()
    {
        MainWindow window = project.Open("Joins.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "=> Join(");
        Ui.Run(window, AppActions.ParameterInfo);
        Ui.WaitFor(() => window.Assist.SignatureText?.Contains("is put between", StringComparison.Ordinal) == true, "Join's parameter info with its separator's documentation");
        string said = window.Assist.SignatureText!;
        Assert.True(said.Contains("The string to use as a separator. separator is put between values only.", StringComparison.Ordinal),
            $"Parameter Info runs the sentence into the parameter its documentation names after it, as though a word were missing:\n{said}");

        // .NET's own, when the reference pack has its documentation.
        if (!project.ReferencePackDownloaded)
        {
            Ui.Press(window, "ESCAPE");
            Ui.PlaceAfter(editor, "+ string.Join(");
            Ui.Run(window, AppActions.ParameterInfo);
            Ui.WaitFor(() => window.Assist.SignatureText?.Contains("IEnumerable<string?> values", StringComparison.Ordinal) == true, "String.Join's parameter info");
            said = window.Assist.SignatureText!;
            Assert.True(said.Contains("separator. separator is included", StringComparison.Ordinal) && !said.Contains("separator.separator", StringComparison.Ordinal),
                $"String.Join's Parameter Info reads 'separator.separator':\n{said}");
        }
        window.Close();
    }

    private static T Await<T>(Task<T> task, string what) => Ui.Await(task, what, TimeSpan.FromSeconds(30));

    /// <summary>Chooses an item of the open list of choices by its words, with the keys a person would press.</summary>
    private static void Choose(MainWindow window, string item)
    {
        int at = window.Assist.Offered.ToList().IndexOf(item);
        Assert.True(at >= 0, $"{item} is not offered: {string.Join("; ", window.Assist.Offered)}");
        Ui.Settle();
        for (int i = 0; i < at; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }

    private void Renaming_a_file_to_match_its_type_with_unsaved_edits_leaves_one_file_of_the_new_name_holding_them()
    {
        string foo = project.PathOf("Foo.cs");
        string bar = project.PathOf("Bar.cs");
        project.Write("Foo.cs", "class Bar\n{\n}\n");
        MainWindow window = project.Open("Foo.cs");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "{");
            window.KeyTextInput(" public int Edited; ");
            Ui.PlaceAfter(editor, "class B");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Rename file to Bar.cs"), "the offer to rename the file");
            for (int i = window.Assist.Offered.ToList().IndexOf("Rename file to Bar.cs"); i > 0; i--)
            {
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => !File.Exists(foo), "Foo.cs to be renamed");

            Assert.True(File.ReadAllText(bar) == "class Bar\n{ public int Edited; \n}\n", "Bar.cs does not hold the edits made before the rename");
            Assert.True(ReferenceEquals(document, Assert.Single(window.EditorArea.Documents)), "The renamed file's tab did not move to Bar.cs");
            Assert.Equal(PathNames.Canonical(bar), document.Path);
            Assert.Equal("Bar.cs", window.EditorArea.Active?.Name);
            Assert.EndsWith("/Bar.cs", window.CSharpService?.For(document)?.Lsp?.Uri, StringComparison.Ordinal);

            window.KeyTextInput("// ");
            window.EditorArea.SaveAll();
            Assert.False(File.Exists(foo), "Saving the moved file wrote Foo.cs back");
            Assert.StartsWith("class B// ar", File.ReadAllText(bar), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            File.Delete(foo);
            File.Delete(bar);
        }
    }

    /// <summary>The hints in one window: types the code leaves out show after their names, a hint's card says what its
    /// type is, and Settings turns type name hints off and on again, parameter name hints staying; the caret steps
    /// across a hint as one place and typing at a name keeps its type after it; and a literal argument shows its
    /// parameter's name before it, the text after making room, until Settings turns them off.</summary>
    private void Type_and_parameter_name_hints_show_where_Rider_shows_them_make_room_step_as_one_place_and_go_with_Settings()
    {
        MainWindow window = project.Open("Inferred.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string code = editor.Document.Snapshot.ToString();
        int After(string text) => code.IndexOf(text, StringComparison.Ordinal) + text.Length;
        Ui.WaitFor(() => editor.InlayHints.Any(h => h.Kind == InlayHintKind.Type), "the type name hints");

        // None where the code shows the type: new List<int>(), a declared Func, a declared List.
        Assert.Equal(
            [
                (":int", After("var total")),
                (":int", After("ForEach(item")),
                ("List<int>", After("Count(new")),
                (":int", After("var (first")),
                (":string", After("var (first, second")),
            ],
            editor.InlayHints.Where(h => h.Kind == InlayHintKind.Type).Select(h => (h.Label, h.Offset)));
        Assert.Equal(["width:", "height:"], editor.InlayHints.Where(h => h.Kind == InlayHintKind.Parameter).Select(h => h.Label));

        double Step(int offset) => editor.PointOf(offset + 1).X - editor.PointOf(offset).X;
        double character = Step(After("var tot"));
        Assert.True(Step(After("var total")) > character * 3, $"'total' and ' =' stand {Step(After("var total"))} apart against a character's {character}");

        Point hint = editor.PointOf(After("var total")) + new Point(character * 1.5, editor.LineHeight / 2);
        window.MouseMove(editor.TranslatePoint(hint, window)!.Value, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the hint's hover card");
        Assert.Contains("Int32", window.Assist.HoverText, StringComparison.Ordinal);
        window.MouseMove(new Point(1, 1), Avalonia.Input.RawInputModifiers.None);
        Ui.Settle();

        int name = After("var total");
        int argument = code.IndexOf("Of(3", StringComparison.Ordinal) + "Of(".Length;
        Assert.True(window.Settings.TypeHints, "Type name hints are not on by default");
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "type name hints";
        Ui.Settle();
        Assert.Equal(SettingsDialog.EditorPage, dialog.Page);
        Assert.Contains("Show type name hints", dialog.Shown);
        dialog.Change(s => s with { TypeHints = false });
        dialog.Ok();
        Ui.Settle();
        Assert.Equal(character, Step(name), 3);
        Assert.True(Step(argument - 1) > character * 4, "Turning type name hints off took the parameter name hints too");
        dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Change(s => s with { TypeHints = true });
        dialog.Ok();
        Ui.Settle();
        Assert.True(Step(name) > character * 3, "Type name hints turned on again did not come back");

        Ui.WaitFor(() => editor.InlayHints.Any(h => h.Kind == InlayHintKind.Type && h.Offset == name), "the hint after 'total'");
        double nameEnd = editor.PointOf(name - 1).X + character;
        double Caret() => editor.PointOf(editor.Selections.Primary.Caret).X;
        Ui.PlaceAfter(editor, "var tota");

        Ui.Press(window, "Right");
        Assert.Equal(name, editor.Selections.Primary.Caret);
        Assert.Equal(nameEnd, Caret(), 1);
        Ui.Press(window, "Right");
        Assert.Equal(name, editor.Selections.Primary.Caret);
        double pastHint = Caret();
        Assert.True(pastHint > nameEnd + 20, $"Right from before the hint left the caret at {pastHint}, the name ending at {nameEnd}");
        Ui.Press(window, "Right");
        Assert.Equal(name + 1, editor.Selections.Primary.Caret);

        Ui.Press(window, "Left");
        Assert.Equal((name, pastHint), (editor.Selections.Primary.Caret, Caret()));
        Ui.Press(window, "Left");
        Assert.Equal(name, editor.Selections.Primary.Caret);
        Assert.Equal(nameEnd, Caret(), 1);
        Ui.Press(window, "Left");
        Assert.Equal(name - 1, editor.Selections.Primary.Caret);

        Ui.PlaceAfter(editor, "Area.Of");
        double parenthesis = Caret();
        Ui.Press(window, "Right");
        Assert.Equal(argument, editor.Selections.Primary.Caret);
        double beforeWidth = Caret();
        Assert.True(beforeWidth - parenthesis < 20, $"Right past the parenthesis left the caret {beforeWidth - parenthesis} on, past 'width:'");
        Ui.Press(window, "Right");
        Assert.Equal(argument, editor.Selections.Primary.Caret);
        Assert.True(Caret() - beforeWidth > 20, "Right before 'width:' did not step the caret past it");

        editor.Selections = SelectionSet.Single(name);
        window.KeyTextInput("s");
        Assert.Contains(editor.InlayHints, h => h.Kind == InlayHintKind.Type && h.Offset == name + 1 && h.Label == ":int");
        Assert.Equal(name + 1, editor.Selections.Primary.Caret);
        Assert.Equal(nameEnd + character, Caret(), 1);

        window.OpenFile(project.PathOf("Area.cs"));
        Ui.Settle();
        int literal = editor.Document.Snapshot.ToString().IndexOf("Of(3, 4)", StringComparison.Ordinal) + "Of(".Length;
        Ui.WaitFor(() => editor.InlayHints.Count == 2, "the parameter name hints");
        Assert.Equal([("width:", literal), ("height:", literal + "3, ".Length)], editor.InlayHints.Select(h => (h.Label, h.Offset)));

        character = Step(literal);
        Assert.True(Step(literal - 1) > character * 4, $"{Step(literal - 1)} against a character's {character}");

        editor.ShowParameterHints = false;
        Assert.Equal(character, Step(literal - 1), 3);
        window.Close();
    }

    /// <summary>With Settings showing neither kind of hint, typing with pauses asks the server for none, though each pause
    /// still pulls the file's diagnostics; turning parameter name hints on brings them at once, the server asked for that
    /// kind alone, and turning type name hints on brings those too. Counted in the protocol's own trace.</summary>
    private void With_both_kinds_of_hint_off_no_hints_are_asked_for_and_turning_one_on_brings_its_hints_at_once()
    {
        MainWindow window = project.Open("Inferred.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string code = editor.Document.Snapshot.ToString();
        int name = code.IndexOf("var total", StringComparison.Ordinal) + "var total".Length;
        Ui.WaitFor(() => editor.InlayHints.Any(h => h.Kind == InlayHintKind.Type), "the type name hints");
        int server = window.CSharpService!.Session!.Server.ProcessId;
        int hintsAsked = 0;
        int pulls = 0;
        void Count() => Passenger.Lsp.LanguageServer.Trace = (from, message) =>
        {
            if (from == server && message.StartsWith("-->", StringComparison.Ordinal))
            {
                if (message.Contains($"\"{Passenger.Lsp.LspMethods.InlayHint}\"", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref hintsAsked);
                }
                else if (message.Contains($"\"{Passenger.Lsp.LspMethods.DocumentDiagnostic}\"", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref pulls);
                }
            }
        };
        void Change(Func<AppSettings, AppSettings> change) => window.ApplySettings(change(window.Settings));
        try
        {
            Change(s => s with { ParameterHints = false, TypeHints = false });
            Count();
            Ui.Settle();
            Assert.True(editor.InlayHints.Count == 0, $"With both kinds of hint off the file still holds {editor.InlayHints.Count} hints");

            Ui.PlaceAfter(editor, "var total = Area.Of(3, 4);");
            editor.Focus();
            for (int i = 0; i < 3; i++)
            {
                int pulled = Volatile.Read(ref pulls);
                window.KeyTextInput($" total += Area.Of({i}, 2);");
                Ui.Settle();
                Assert.True(Volatile.Read(ref pulls) > pulled, $"The pause after statement {i + 1} pulled no diagnostics");
            }
            Assert.True(Volatile.Read(ref hintsAsked) == 0, $"With both kinds of hint off, three pauses in typing asked the server for hints {hintsAsked} times");
            Assert.True(editor.InlayHints.Count == 0, $"With both kinds of hint off the file holds {editor.InlayHints.Count} hints");

            Change(s => s with { ParameterHints = true });
            Ui.WaitFor(() => editor.InlayHints.Count(h => h.Label == "width:") == 4, "the four calls' parameter name hints, once they were turned on");
            Assert.True(Volatile.Read(ref hintsAsked) > 0, "Parameter name hints came without the server being asked for them");
            Assert.True(editor.InlayHints.All(h => h.Kind == InlayHintKind.Parameter),
                $"With type name hints off the server still made {string.Join(", ", editor.InlayHints.Where(h => h.Kind != InlayHintKind.Parameter).Select(h => h.Label))}");

            Change(s => s with { TypeHints = true });
            Ui.WaitFor(() => editor.InlayHints.Any(h => h.Kind == InlayHintKind.Type && h.Offset == name && h.Label == ":int"),
                "the type name hint after 'total', once they were turned on");
        }
        finally
        {
            Passenger.Lsp.LanguageServer.Trace = null;
            window.Close();
        }
    }

    private void Extend_selection_grows_through_the_code_around_the_caret_and_shrink_takes_it_back()
    {
        MainWindow window = project.Open("Loop.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "            to");
        string Selected() => editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);
        List<string> grown = [];
        for (int i = 0; i < 6; i++)
        {
            Ui.Run(window, AppActions.ExtendSelection);
            grown.Add(Selected());
        }
        Assert.Equal("total", grown[0]);
        Assert.Equal("total += i * 2", grown[1]);
        Assert.Equal("total += i * 2;", grown[2]);
        Assert.StartsWith("{", grown[3], StringComparison.Ordinal);
        Assert.StartsWith("for (", grown[4], StringComparison.Ordinal);
        Assert.StartsWith("int total = 0;", grown[5], StringComparison.Ordinal);
        Assert.EndsWith("return total;", grown[5], StringComparison.Ordinal);

        Ui.Run(window, AppActions.ShrinkSelection);
        Ui.Run(window, AppActions.ShrinkSelection);
        Assert.Equal(grown[3], Selected());
        for (int i = 0; i < 4; i++)
        {
            Ui.Run(window, AppActions.ShrinkSelection);
        }
        Assert.True(editor.Selections.Primary.IsEmpty);
        window.Close();
    }

    /// <summary>Live and postfix templates in one window: an abbreviation and Tab become a live template where it
    /// belongs (prop in a type's body, cw in a method's); a live template fills every place of a variable at once, Tab
    /// goes on and back, and a caret moved away ends it; and a postfix template after an expression and a dot wraps it
    /// where it fits, asking which expression when more than one could be meant.</summary>
    private void Live_and_postfix_templates_expand_where_they_belong_fill_their_variables_and_wrap_what_they_follow()
    {
        MainWindow window = project.Open("Point.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        string Text() => editor.Document.Snapshot.ToString();
        string Selected() => editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length);
        Ui.PlaceAfter(editor, "public int Y { get; }");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("prop");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        Assert.Contains("    public int Name { get; set; }", Text(), StringComparison.Ordinal);
        Assert.Equal("int", Selected());
        window.KeyTextInput("string");
        Ui.Press(window, "TAB");
        window.KeyTextInput("Label");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet);
        Assert.Contains("    public string Label { get; set; }\n}", Text(), StringComparison.Ordinal);

        window.OpenFile(project.PathOf("Loop.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("cw");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Contains("        Console.WriteLine();\n", Text(), StringComparison.Ordinal);
        Assert.Equal(Text().IndexOf("Console.WriteLine(", StringComparison.Ordinal) + "Console.WriteLine(".Length, editor.Selections.Primary.Caret);
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "int total = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("for");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        Assert.Equal(3, editor.Selections.Count);
        window.KeyTextInput("row");
        Assert.Contains("        for (int row = 0; row < length; row++)\n        {\n", Text(), StringComparison.Ordinal);
        Ui.Press(window, "TAB");
        Assert.Equal("length", Selected());
        window.KeyTextInput("rows");
        Ui.Press(window, "shift TAB");
        Assert.Equal(3, editor.Selections.Count);
        Ui.Press(window, "TAB");
        Ui.Press(window, "TAB");
        Assert.False(editor.IsInSnippet);
        string loop = "        for (int row = 0; row < rows; row++)\n        {\n            \n        }\n";
        Assert.Contains(loop, Text(), StringComparison.Ordinal);
        Assert.Equal(Text().IndexOf(loop, StringComparison.Ordinal) + loop.IndexOf("            \n", StringComparison.Ordinal) + 12, editor.Selections.Primary.Caret);

        Ui.Press(window, "ENTER");
        window.KeyTextInput("thr");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        Ui.Press(window, "UP");
        Assert.False(editor.IsInSnippet, "The template went on with the caret off its variables");
        Assert.Contains("throw new InvalidOperationException();", Text(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "int total = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Enumerable.Range(0, 3).foreach");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Contains("        foreach (var item in Enumerable.Range(0, 3))\n        {\n", Text(), StringComparison.Ordinal);
        Assert.Equal("item", Selected());
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "int total = 0;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("total.var");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Contains("        var value = total;", Text(), StringComparison.Ordinal);
        window.KeyTextInput("copy");
        Ui.Press(window, "TAB");
        Assert.Contains("        var copy = total;", Text(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "var copy = total;");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("int doubled = 2 * total.par");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.Equal(["total", "2 * total"], window.Assist.Offered);
        Ui.Press(window, "ENTER");
        Assert.Contains("        int doubled = 2 * (total)\n", Text(), StringComparison.Ordinal);
        Ui.Press(window, "ESCAPE");
        window.KeyTextInput(" + total.if");
        Ui.Settle();
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "TAB");
        Assert.DoesNotContain("if (", Text(), StringComparison.Ordinal);
        window.Close();
    }

    private void A_live_template_ends_when_its_editor_shows_another_file_and_enter_there_breaks_the_line()
    {
        MainWindow window = project.Open("Helper.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "value * 2;");
        string helper = editor.Document.Snapshot.ToString();
        window.OpenFile(project.PathOf("Loop.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("for");
        Ui.Settle();
        Ui.Press(window, "TAB");
        Assert.True(editor.IsInSnippet);
        string loop = editor.Document.Snapshot.ToString();

        Ui.Run(window, AppActions.NextTab);
        Assert.Equal(PathNames.Canonical(project.PathOf("Helper.cs")), window.EditorArea.Active?.Path);
        Ui.Press(window, "ENTER");
        int end = helper.IndexOf("value * 2;", StringComparison.Ordinal) + "value * 2;".Length;
        Assert.True(editor.Document.Snapshot.ToString() == helper.Insert(end, "\n    "),
            $"Enter in Helper.cs stepped through Loop.cs's template instead of breaking the line:\n{editor.Document.Snapshot}");
        Assert.Equal(new TextPosition(3, 4), editor.CaretPosition);
        Assert.False(editor.IsInSnippet, "Helper.cs is drawn with boxes at the places of Loop.cs's template");

        Ui.Run(window, AppActions.NextTab);
        Assert.False(editor.IsInSnippet, "Loop.cs's template took up again after its file was left");
        Assert.Equal(loop, editor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>A class and its caller another program wrote a moment ago (an agent in the terminal), heard by the window but
    /// not yet taken into the project by the language server: Find Usages on the class's method, opened at once, lists the
    /// caller too, where an answer from the server's Miscellaneous Files lists the declaration alone.</summary>
    private void Find_usages_in_a_file_another_program_has_just_written_lists_the_caller_it_wrote_beside_it()
    {
        MainWindow window = project.Open("Program.cs");
        HashSet<string> heard = new(StringComparer.Ordinal);
        EventHandler<FolderChanges> hear = (_, changes) => heard.UnionWith(changes.Paths.Select(Path.GetFileName).OfType<string>());
        window.FolderWatcher!.Changed += hear;
        File.WriteAllText(project.PathOf("Triangle.cs"), "class Triangle\n{\n    public static Triangle Make() => new();\n}\n");
        File.WriteAllText(project.PathOf("UseTriangle.cs"), "static class UseTriangle\n{\n    public static Triangle Run() => Triangle.Make();\n}\n");
        Ui.PumpUntil(() => heard.Contains("Triangle.cs") && heard.Contains("UseTriangle.cs"), "the folder's watcher to hear of both files");
        window.FolderWatcher.Changed -= hear;
        window.OpenFile(project.PathOf("Triangle.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Triangle Ma");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => window.Find.Rows.Any(r => r.Usage is not null), "the usages in the Find window");
        string[] usages = [.. window.Find.Rows.Where(r => r.Usage is not null).Select(r => r.Label)];
        Assert.True(usages.Contains("public static Triangle Run() => Triangle.Make();"),
            $"Find Usages on a method just written missed the caller written beside it, so a rename would leave it behind: [{string.Join("; ", usages)}] ({window.Status})");
        window.Close();
    }

    /// <summary>Find Usages' keys put the keys on its results, the first usage chosen, so Down goes on to the next usage
    /// and Enter opens it, where the keys stayed in the editor and Down moved its caret.</summary>
    private void Find_usages_gives_its_results_the_keys_so_Down_and_Enter_open_the_next_usage()
    {
        MainWindow window = project.Open("Loop.cs");
        FindView find = window.Find;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "int tot");
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => find.Rows.Count(r => r.Usage is not null) == 3, "the usages in the Find window");
        Avalonia.Controls.ListBox list = find.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single();
        Assert.True(window.FocusManager?.GetFocusedElement() is Avalonia.Visual focused && list.IsVisualAncestorOf(focused),
            $"Find Usages left the keys on {window.FocusManager?.GetFocusedElement()?.GetType().Name}, not its results");
        List<FindRow> uses = [.. find.Rows.Where(r => r.Usage is not null)];
        Assert.Same(uses[0], find.Selected);
        List<FindRow> rows = [.. find.Rows];
        for (int at = rows.IndexOf(uses[0]); at < rows.IndexOf(uses[1]); at++)
        {
            Ui.Press(window, "DOWN");
        }
        Assert.Same(uses[1], find.Selected);
        Ui.Press(window, "ENTER");
        Usage second = uses[1].Usage!;
        Assert.True(window.EditorArea.CodeEditor.CaretPosition == new TextPosition(second.Line, second.Column) && window.EditorArea.CodeEditor.IsFocused,
            $"Down and Enter in Find Usages' results left the caret at {window.EditorArea.CodeEditor.CaretPosition}, not at the second usage {second.Line}:{second.Column}");
        Assert.Equal(File.ReadAllText(project.PathOf("Loop.cs")), window.EditorArea.Active!.Text.Snapshot.ToString());
        window.Close();
    }

    private void Call_and_type_hierarchies_show_who_calls_a_method_and_what_derives_from_a_type()
    {
        MainWindow window = project.Open("Helper.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "static int Tw");
        Assert.Null(Ui.Await(window.ShowCallHierarchyAsync(), "the call hierarchy"));
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Hierarchy));
        Assert.Equal(2, window.Hierarchy.Rows.Count);
        Assert.Contains("Twice", window.Hierarchy.Rows[0].Node.Item.Name, StringComparison.Ordinal);
        Assert.Contains("Main", window.Hierarchy.Rows[1].Node.Item.Name, StringComparison.Ordinal);
        HierarchyRow caller = window.Hierarchy.Rows[1];
        Assert.Equal(PathNames.Canonical(project.PathOf("Program.cs")), caller.Node.Path);
        Assert.Equal(4, caller.Node.Where.Line);
        TreeKeysCheck.Press(window, window.Hierarchy.Rows, window.Hierarchy.Rows[0], "the Hierarchy window", enterOpens: false);
        Ui.Press(window, "Right");
        Ui.Press(window, "Down");
        Ui.Press(window, "Enter");
        Assert.True(window.EditorArea.Active?.Path == caller.Node.Path, "Enter on a caller in the Hierarchy window did not go to its call");

        Task callees = window.Hierarchy.ChooseAsync(first: false);
        Ui.WaitFor(() => callees.IsCompleted, "the callees");
        Assert.False(window.Hierarchy.IsFirst);

        window.OpenFile(project.PathOf("Shapes.cs"));
        Ui.Settle();
        Ui.PlaceAfter(editor, "interface ISh");
        Task<string?> types = window.ShowTypeHierarchyAsync();
        Ui.WaitFor(() => types.IsCompleted, "the type hierarchy");
        Task subtypes = window.Hierarchy.ChooseAsync(first: false);
        Ui.WaitFor(() => subtypes.IsCompleted, "the subtypes");
        Assert.Contains(window.Hierarchy.Rows, r => r.Depth == 1 && r.Node.Item.Name.Contains("Square", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>Call Hierarchy at a use of a method declared in another file shows it and its callers as at its declaration,
    /// and at a record's positional property, from a use in another file or at its declaration, shows the property with the
    /// members that read or set it, each at its first use, as for any property; what the property calls is nothing, as an
    /// auto-property's.</summary>
    private void Call_hierarchy_at_a_use_in_another_file_and_at_a_records_positional_property_shows_its_callers()
    {
        MainWindow window = project.Open("Program.cs");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Helper.Tw");
        Assert.Null(Ui.Await(window.ShowCallHierarchyAsync(), "the call hierarchy at a use of Twice"));
        Assert.Equal(["Helper.Twice(int) Helper.cs:3", "  Program.Main() Program.cs:5"], Listed());

        foreach ((string file, string at) in new[] { ("ReleaseReaders.cs", "release.Vers"), ("Releases.cs", "record Release(string Vers") })
        {
            window.OpenFile(project.PathOf(file));
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, at);
            Assert.Null(Ui.Await(window.ShowCallHierarchyAsync(), $"the call hierarchy after {at}"));
            Assert.Equal("Calls of Release.Version", window.Hierarchy.Title);
            Assert.Equal(["Release.Version Releases.cs:1", "  ReleaseReaders.Of(Release) ReleaseReaders.cs:3", "  ReleaseReaders.Next(Release) ReleaseReaders.cs:5"], Listed());
        }
        Task callees = window.Hierarchy.ChooseAsync(first: false);
        Ui.WaitFor(() => callees.IsCompleted, "what Release.Version calls");
        Assert.Equal(["Release.Version Releases.cs:1"], Listed());
        window.Close();

        string[] Listed() => [.. window.Hierarchy.Rows.Select(r => $"{new string(' ', r.Depth * 2)}{r.Node.Item.Name} {Path.GetFileName(r.Node.Path)}:{r.Node.Where.Line + 1}")];
    }

    /// <summary>Typing on into an open completion list narrows it off the UI thread in the rows the list has: as each letter
    /// narrows a class's members, the controls on screen are the ones that showed it before; and a key into a list of four
    /// times the items costs the UI thread what it does in the shorter one, counted rather than timed. The lists differ in
    /// length alone: a class's members after its dot, listed whole by the server, its fields named alike, so both hold as
    /// many rows narrowed and show the same ones.</summary>
    private void Typing_into_a_completion_list_narrows_it_off_the_UI_thread_in_the_rows_it_shows()
    {
        MainWindow window = Ui.OpenFolder(project.Folder, autosaves: false);
        window.OpenFile(project.PathOf("Pantry.cs"));
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        (int fewItems, long fewKey) = Narrowed(window, project.PathOf("Pantry.cs"));
        (int manyItems, long manyKey) = Narrowed(window, project.PathOf("Granary.cs"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key narrowing a list of {fewItems} members allocated {fewKey / 1024} KB on the UI thread; of {manyItems} members, {manyKey / 1024} KB");
        Assert.True(manyItems >= fewItems * 4, $"Granary's members listed {manyItems} items, against Pantry's {fewItems}");
        // Less than the least object for each item the longer list has more: whatever is made of an item is made off it.
        Assert.True(manyKey - fewKey < (manyItems - fewItems) * 24L,
            $"A key narrowing a list of {manyItems} items allocated {manyKey / 1024} KB on the UI thread, against {fewKey / 1024} KB for {fewItems} items, "
            + $"{(manyKey - fewKey) / (manyItems - fewItems)} bytes for each item more: it scored them or made their rows there");
        window.Close();
    }

    /// <summary>A class's members, listed after its dot typed in its own method, and narrowed a letter at a time: how many
    /// items the list has, and the least the UI thread allocates from a key to the narrower list shown; each key's rows
    /// shown in the controls the list showed before it.</summary>
    private static (int Items, long Key) Narrowed(MainWindow window, string path)
    {
        string type = Path.GetFileNameWithoutExtension(path);
        window.OpenFile(path);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "        int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput($"total += {type}");
        Ui.Settle();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, $"{type}'s members after its dot");
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
        int caret = editor.Selections.Primary.Caret;
        int items = Ui.Await(Waiting.Apart(() => lsp.CompletionAsync(caret, ".")), $"{type}'s members").List?.Items.Count ?? 0;
        window.KeyTextInput("R");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "R", $"{type}'s members narrowed by R");
        string typed = "R";
        long key = long.MaxValue;
        foreach (string letter in new[] { "a", "t", "i" })
        {
            IReadOnlyList<Avalonia.Controls.Control> shown = window.Assist.CompletionRowControls;
            Assert.True(shown.Count > 0, $"No row of the list is on screen narrowed by '{typed}'");
            typed += letter;
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.KeyTextInput(letter);
            Ui.Pumped(() => window.Assist.CompletionNarrowedBy == typed, TimeSpan.FromSeconds(30));
            key = Math.Min(key, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(window.Assist.CompletionNarrowedBy == typed, $"The list never showed its rows for '{typed}'");
            Assert.True(window.Assist.CompletionLabels.FirstOrDefault() == "Ration000",
                $"Narrowed by '{typed}' the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(5))} first");
            Ui.Settle();
            IReadOnlyList<Avalonia.Controls.Control> now = window.Assist.CompletionRowControls;
            Assert.True(now.Count > 0 && now.All(row => shown.Contains(row)),
                $"Narrowed by '{typed}', {now.Count(row => !shown.Contains(row))} of the list's {now.Count} rows on screen are controls made for the key");
        }
        Ui.Press(window, "ESCAPE");
        return (items, key);
    }

    /// <summary>The completion list shows its rows in ten places kept for good, so a key that changes how many rows it
    /// shows lays out only those that came onto the screen or left it: narrowing a class's members from 200 rows to 100, 10
    /// and 1 and widening them back costs the UI thread, key for key, less than a key that hands every row on screen another
    /// member does, counted rather than timed; so does each Down once the list scrolls, which hands each row the next member.</summary>
    private void A_key_that_changes_how_many_rows_the_completion_list_shows_lays_out_only_the_rows_that_came_or_went()
    {
        MainWindow window = Ui.OpenFolder(project.Folder, autosaves: false);
        window.OpenFile(project.PathOf("Pantry.cs"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        Ui.PlaceAfter(editor, "        int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("total += Pantry");
        Ui.Settle();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen, "Pantry's members after its dot");
        window.KeyTextInput("Ration");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "Ration" && window.Assist.CompletionLabels.Count == 200,
            () => $"Pantry's members narrowed by Ration to the 200 shown, where the list shows {window.Assist.CompletionLabels.Count}");
        string typed = "Ration";
        long reworded = long.MaxValue;
        Dictionary<string, long> changed = [];
        for (int round = 0; round < 3; round++)
        {
            // Ration100 to Ration109 in place of Ration000 to Ration009: every row on screen another member.
            reworded = Math.Min(reworded, Key("1", 100));
            Key("BACK_SPACE", 200);
            foreach ((string key, int rows, string what) in new[]
            {
                ("0", 100, "200 rows to 100, the same on screen"), ("0", 10, "100 rows to 10, the same on screen"), ("0", 1, "10 rows to 1, 9 leaving the screen"),
                ("BACK_SPACE", 10, "1 row to 10, 9 coming back"), ("BACK_SPACE", 100, "10 rows to 100, the same on screen"), ("BACK_SPACE", 200, "100 rows to 200, the same on screen"),
            })
            {
                long cost = Key(key, rows);
                changed[what] = Math.Min(changed.GetValueOrDefault(what, long.MaxValue), cost);
            }
        }
        // Past the rows first on screen, the list scrolled.
        for (int i = 0; i < 12; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Assert.Equal("Ration012", window.Assist.CompletionChosen);
        long scrolled = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Ui.Press(window, "DOWN", settle: false);
            Ui.Draw();
            scrolled = Math.Min(scrolled, GC.GetAllocatedBytesForCurrentThread() - before);
            Ui.Settle();
        }
        Assert.Equal("Ration015", window.Assist.CompletionChosen);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key handing every row on screen another member: {reworded / 1024} KB on the UI thread; "
            + string.Join("; ", changed.Select(c => $"{c.Key}: {c.Value / 1024} KB")) + $"; a Down scrolling the list: {scrolled / 1024} KB");
        foreach ((string what, long cost) in changed)
        {
            Assert.True(cost < reworded,
                $"A key taking Pantry's members from {what} allocated {cost / 1024} KB on the UI thread, against {reworded / 1024} KB for a key handing every row on screen another member: "
                + "it laid out rows that neither came nor went, or made their places anew");
        }
        Assert.True(scrolled < reworded,
            $"A Down scrolling the completion list a row allocated {scrolled / 1024} KB on the UI thread, against {reworded / 1024} KB for a key handing every row on screen another member: "
            + "the scroll laid its rows out anew");
        Ui.Press(window, "ESCAPE");
        window.Close();

        // The least the UI thread allocates from a key to the list it leaves drawn, the list then showing as many rows.
        long Key(string key, int rows)
        {
            typed = key == "BACK_SPACE" ? typed[..^1] : typed + key;
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (key == "BACK_SPACE")
            {
                Ui.Press(window, key, settle: false);
            }
            else
            {
                window.KeyTextInput(key);
            }
            Ui.Pumped(() => window.Assist.CompletionNarrowedBy == typed, TimeSpan.FromSeconds(30));
            Ui.Draw();
            long cost = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(window.Assist.CompletionNarrowedBy == typed && window.Assist.CompletionLabels.Count == rows,
                $"Narrowed by '{typed}' the list shows {window.Assist.CompletionLabels.Count} rows, not {rows}");
            Ui.Settle();
            return cost;
        }
    }

    /// <summary>A window's completion list is laid out once as the window opens, off screen: its first list shows in row
    /// controls made then, so the first list a person asks for waits for no control to be made, styled or shaped.</summary>
    private void A_windows_first_completion_list_shows_in_the_rows_laid_out_as_the_window_opened()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Settle();
        IReadOnlyList<Avalonia.Controls.Control> ahead = window.Assist.CompletionRowsLaidOut;
        Assert.True(ahead.Count >= 10 && !window.Assist.IsCompletionOpen,
            $"The window opened with {ahead.Count} completion rows laid out, the list {(window.Assist.IsCompletionOpen ? "showing" : "hidden")}: its first list would make its own");
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("S");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "S", "a statement's list narrowed by S");
        IReadOnlyList<Avalonia.Controls.Control> shown = window.Assist.CompletionRowControls;
        Assert.True(shown.Count > 0 && shown.All(ahead.Contains),
            $"The window's first list showed {shown.Count(row => !ahead.Contains(row))} of its {shown.Count} rows in controls it made then, not those laid out as the window opened");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>A statement's first letters show what is in scope while the server's types of namespaces not imported, held
    /// unanswered, are still to come; once they come they join the list, the row chosen meanwhile still chosen and the
    /// caret where it was, and typing on asks the server for no list. A name nothing in scope fits shows no list until
    /// they come, then theirs.</summary>
    private void A_statements_first_rows_show_before_the_types_of_namespaces_not_imported_which_join_them_leaving_the_choice()
    {
        MainWindow window = project.Open("Program.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "int result = Helper.Twice(2);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        Ui.Settle();
        // Roslyn lists the types of namespaces not imported once it has indexed them, so the name is typed again until it has.
        Ui.WaitFor(() =>
        {
            Typed("Str");
            bool indexed = window.Assist.CompletionLabels.Contains("StringBuilder");
            Ui.Press(window, "ESCAPE");
            Erased(3);
            if (!indexed)
            {
                Ui.Hold(TimeSpan.FromMilliseconds(250), "a pause before the name is typed again, as a person retries");
            }
            return indexed;
        }, "StringBuilder, of a namespace Program.cs does not import, in a statement's list");
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;

        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldNextAnswer(Passenger.Roslyn.Extension.ExtensionMethods.LaterCompletion, _ => true, released.Task);
        try
        {
            TypedPumped("St");
            Ui.AwaitPumped(held, "the types of namespaces not imported, come and held unanswered");
            IReadOnlyList<string> inScope = window.Assist.CompletionLabels;
            Assert.True(inScope.Count > 1 && inScope.Contains("string") && !inScope.Contains("String"),
                $"Narrowed by 'St' before the types of namespaces not imported came, the list showed [{string.Join(", ", inScope)}]");
            Ui.Press(window, "DOWN", settle: false);
            Ui.PumpUntil(() => window.Assist.CompletionChosen == inScope[1], $"{inScope[1]}, the second row, chosen");
            int caret = editor.Selections.Primary.Caret;
            released.SetResult();
            Ui.WaitFor(() => window.Assist.CompletionLabels.Any(label => !inScope.Contains(label)), "the types of namespaces not imported joining the list as they came");
            IReadOnlyList<string> joined = window.Assist.CompletionLabels;
            Assert.True(window.Assist.CompletionChosen == inScope[1] && editor.Selections.Primary.Caret == caret && window.Assist.CompletionNarrowedBy == "St",
                $"As the types of namespaces not imported joined the list, the chosen row went from {inScope[1]} to {window.Assist.CompletionChosen}, the caret from {caret} "
                + $"to {editor.Selections.Primary.Caret}: Enter would put in another name than the one chosen");
            Assert.True(inScope.Where(joined.Contains).SequenceEqual(joined.Where(inScope.Contains)),
                $"What was in scope showed [{string.Join(", ", inScope)}], and joined by the rest [{string.Join(", ", joined.Take(12))}…]: one list in two orders");

            Task asked = server.HoldNext(lsp.CompletionMethod, _ => true, Task.CompletedTask);
            Typed("r");
            Assert.True(window.Assist.CompletionLabels.Contains("StringBuilder"), $"Narrowed by 'Str' the list joined showed [{string.Join(", ", window.Assist.CompletionLabels.Take(12))}…]");
            Assert.False(asked.IsCompleted, "Typing on into the list joined by the types of namespaces not imported asked the server for the list again");
            Ui.Press(window, "ESCAPE");
            Erased(3);
        }
        finally
        {
            released.TrySetResult();
        }

        TaskCompletionSource releasedAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task heldAgain = server.HoldNextAnswer(Passenger.Roslyn.Extension.ExtensionMethods.LaterCompletion, _ => true, releasedAgain.Task);
        try
        {
            TypedPumped("String");
            window.KeyTextInput("B");
            window.KeyTextInput("u");
            Ui.AwaitPumped(heldAgain, "the types of namespaces not imported, come and held unanswered");
            Ui.PumpUntil(() => !window.Assist.IsCompletionOpen, "the list closed on 'StringBu', which nothing in scope fits");
            releasedAgain.SetResult();
            Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "StringBuilder", "StringBuilder listed as the types of namespaces not imported came");
            Assert.Equal("StringBu", window.Assist.CompletionNarrowedBy);
            Ui.Press(window, "ESCAPE");
        }
        finally
        {
            releasedAgain.TrySetResult();
        }
        window.Close();

        void Typed(string letters)
        {
            foreach (char letter in letters)
            {
                window.KeyTextInput(letter.ToString());
                Ui.Settle();
            }
        }

        void Erased(int letters)
        {
            for (int i = 0; i < letters; i++)
            {
                Ui.Press(window, "BACK_SPACE");
            }
        }

        // Each letter's rows awaited without waiting for the IDE to be quiet, which it is not while an answer is held.
        void TypedPumped(string letters)
        {
            string typed = string.Empty;
            foreach (char letter in letters)
            {
                typed += letter;
                window.KeyTextInput(letter.ToString());
                Ui.PumpUntil(() => window.Assist.CompletionNarrowedBy == typed, $"the list narrowed by '{typed}'");
            }
        }
    }

    /// <summary>A statement's list is the server's whole list, asked for once for the name: each key typed on narrows it here
    /// with no word to the server, and costs the UI thread what a key into a far shorter list does, counted rather than
    /// timed. The lists differ in length alone: a statement in Pantry's method and in Silo's lists each class's fields
    /// and the same rest, Silo's thousands more than the thousand the server cuts a list it is let cut to.</summary>
    private void A_statements_whole_list_narrows_here_as_its_name_is_typed_asking_the_server_nothing()
    {
        MainWindow window = Ui.OpenFolder(project.Folder, autosaves: false);
        window.OpenFile(project.PathOf("Pantry.cs"));
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        (int fewItems, long fewKey) = StatementNarrowed(window, project.PathOf("Pantry.cs"));
        (int manyItems, long manyKey) = StatementNarrowed(window, project.PathOf("Silo.cs"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key narrowing a statement's list of {fewItems} items allocated {fewKey / 1024} KB on the UI thread; of {manyItems} items, {manyKey / 1024} KB");
        Assert.True(manyItems > 1000 && manyItems >= fewItems + 3700,
            $"A statement in Silo listed {manyItems} items and one in Pantry {fewItems}: the server cut the longer list");
        // Less than the least object for each item the longer list has more: whatever is made of an item is made off it.
        Assert.True(manyKey - fewKey < (manyItems - fewItems) * 24L,
            $"A key narrowing a statement's list of {manyItems} items allocated {manyKey / 1024} KB on the UI thread, against {fewKey / 1024} KB for {fewItems} "
            + $"items, {(manyKey - fewKey) / (manyItems - fewItems)} bytes for each item more: it scored them or made their rows there");
        window.Close();
    }

    /// <summary>A statement's list of more than 10,000 items, a class's 13,500 fields among the types in reach, lists the
    /// 12,000 the name typed finds alike in the server's order, from its 1,000s on: none of those past its 10,000th item
    /// ahead of one before; and the server still describes the list's last item, which it finds by its sort text.</summary>
    private void A_statements_list_of_more_than_ten_thousand_lists_the_names_found_alike_in_the_servers_order()
    {
        string folder = PathNames.Canonical(TestFolders.Create("assist-lots-"));
        TestFolders.Write(folder, "Lots.csproj", TestProjects.Console());
        string yard = TestFolders.Write(folder, "Yard.cs", "static class Yard\n{\n" + string.Concat(Enumerable.Range(0, 1_500).Select(i => $"    public static int Bin{i:0000};\n"))
            + string.Concat(Enumerable.Range(0, 12_000).Select(i => $"    public static int Lot{i:00000};\n"))
            + "\n    public static int Tally()\n    {\n        int total = 0;\n        return total;\n    }\n}\n");
        Restore.Run(Path.Combine(folder, "Lots.csproj"));
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        try
        {
            window.OpenFile(yard);
            Ui.Settle();
            Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "        int total = 0;");
            editor.Focus();
            Ui.Press(window, "ENTER");
            Ui.Settle();
            string typed = string.Empty;
            foreach (string letter in new[] { "L", "o", "t" })
            {
                typed += letter;
                window.KeyTextInput(letter);
                Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == typed, $"a statement's list narrowed by {typed}");
            }
            Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
            int caret = editor.Selections.Primary.Caret;
            Passenger.Lsp.CompletionList whole = Ui.Await(Waiting.Apart(() => lsp.CompletionAsync(caret, null)), "a statement's list").List!;
            List<string> served = [.. whole.Items.Select(i => i.Label).Where(l => l.StartsWith("Lot", StringComparison.Ordinal))];
            int first = whole.Items.FindIndex(i => i.Label.StartsWith("Lot", StringComparison.Ordinal));
            Assert.True(first >= 1_000 && whole.Items.Count > 10_000 && whole.Items.Skip(10_000).Any(i => i.Label.StartsWith("Lot", StringComparison.Ordinal)),
                $"A statement's list of {whole.Items.Count} items has the fields from its {first}th, not from its 1,000s to past its 10,000th, which this test is about");
            IReadOnlyList<string> shown = window.Assist.CompletionLabels;
            Assert.True(shown.Count > 0 && shown.SequenceEqual(served.Take(shown.Count)),
                $"Narrowed by 'Lot' the list shows {string.Join(", ", shown.Take(5))} first, where the server lists {string.Join(", ", served.Take(5))} first: "
                + "names the server lists past its 10,000th item go ahead of those it lists before them");
            Passenger.Lsp.CompletionItem last = whole.Items[^1];
            Passenger.Lsp.CompletionItem? described = Ui.Await(Waiting.Apart(() => lsp.ResolveAsync(last)), $"{last.Label} described");
            Assert.True(described?.Documentation is not null,
                $"The server described nothing of {last.Label}, the list's last item, sorted by {last.SortText}: it found no item of its list that sorts so");
            Ui.Press(window, "ESCAPE");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A statement begun in a class's own method and its name typed a letter at a time: how many items the list
    /// has, whole, and the least the UI thread allocates from a key to the narrower list shown, the server asked nothing
    /// after the list's first letter.</summary>
    private static (int Items, long Key) StatementNarrowed(MainWindow window, string path)
    {
        string type = Path.GetFileNameWithoutExtension(path);
        window.OpenFile(path);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "        int total = 0;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        Ui.Settle();
        window.KeyTextInput("R");
        Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "R" && window.Assist.CompletionLabels.Contains("Ration000"), $"a statement's list in {type} narrowed by R");
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
        int caret = editor.Selections.Primary.Caret;
        Passenger.Lsp.CompletionList? whole = Ui.Await(Waiting.Apart(() => lsp.CompletionAsync(caret, null)), $"a statement's list in {type}").List;
        Assert.True(whole is { IsIncomplete: false }, $"The server said a statement's list of {whole?.Items.Count} items in {type} left items out");
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task asked = window.CSharpService!.Session!.Server.HoldNext(lsp.CompletionMethod, _ => true, released.Task);
        string typed = "R";
        long key = long.MaxValue;
        try
        {
            foreach (string letter in new[] { "a", "t", "i", "o", "n" })
            {
                typed += letter;
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyTextInput(letter);
                Ui.Pumped(() => window.Assist.CompletionNarrowedBy == typed, TimeSpan.FromSeconds(30));
                key = Math.Min(key, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.True(window.Assist.CompletionNarrowedBy == typed, $"The list never showed its rows for '{typed}'");
                Ui.Settle();
                Assert.False(asked.IsCompleted, $"Typing '{letter}' into a statement's whole list in {type} asked the server for the list again");
            }
            Assert.True(window.Assist.CompletionLabels.FirstOrDefault() == "Ration000",
                $"Narrowed by '{typed}' the list shows {string.Join(", ", window.Assist.CompletionLabels.Take(5))} first");
        }
        finally
        {
            released.TrySetResult();
        }
        Ui.Press(window, "ESCAPE");
        return (whole!.Items.Count, key);
    }

    /// <summary>Two methods laid out wrongly, the second's first line indented two spaces too far.</summary>
    private const string SelectedLines = "static class Selected\n{\n    public static int First( int a,int b )\n    {\n        return a+b;\n    }\n\n"
        + "      public static int Second(int a, int b)\n    {\n        return a * b;\n    }\n}\n";

    /// <summary>A method with a line indented a level too far, in six spaces as the file has them.</summary>
    private const string Sixes = "static class Sixes\n{\n      public static int Value()\n      {\n            int total = 1;\n                  return total;\n      }\n}\n";

    /// <summary>Two methods each with a line as badly indented as the other's, in four spaces as the file has them.</summary>
    private const string Ragged = "static class Ragged\n{\n    public static int Untouched()\n    {\n        int one = 1;\n            return one;\n    }\n\n"
        + "    public static int Chosen()\n    {\n        int two = 2;\n            return two;\n    }\n}\n";

    /// <summary>A small console project on disk, restored once for every test, and the warm server they share on it.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("assist-")
        {
            File.WriteAllText(PathOf("Sample.csproj"),
                TestProjects.Console());
            File.WriteAllText(PathOf("Program.cs"), "class Program\n{\n    static void Main()\n    {\n        int result = Helper.Twice(2);\n    }\n}\n");
            File.WriteAllText(PathOf("Helper.cs"), "static class Helper\n{\n    public static int Twice(int value) => value * 2;\n}\n");
            File.WriteAllText(PathOf("Shapes.cs"), "interface IShape\n{\n    int Sides { get; }\n}\n\nclass Square : IShape\n{\n    public int Sides => 4;\n}\n");
            File.WriteAllText(PathOf("Area.cs"), "static class Area\n{\n    public static int Of(int width, int height) => width * height;\n    public static int Room() => Of(3, 4);\n}\n");
            File.WriteAllText(PathOf("Point.cs"), "class Point\n{\n    public int X { get; }\n    public int Y { get; }\n}\n");
            File.WriteAllText(PathOf("Selected.cs"), SelectedLines);
            File.WriteAllText(PathOf("Figures.cs"), "class Figure\n{\n    public virtual double Perimeter() => 0;\n    public virtual string Describe(string prefix) => prefix;\n}\n\n"
                + "class Disc : Figure\n{\n    public double Radius { get; set; }\n}\n");
            File.WriteAllText(PathOf("Loop.cs"), "static class Loop\n{\n    public static int Sum()\n    {\n        int total = 0;\n        for (int i = 0; i < 10; i++)\n        {\n            total += i * 2;\n        }\n        return total;\n    }\n}\n");
            // Roslyn ends a line at each of these in the comment; the editor's lines end only at '\n'.
            File.WriteAllText(PathOf("Separators.cs"), "static class Separators\n{\n    /* Pasted:\u2028a line separator,\u2029a paragraph separator,\u0085a next line,\ra carriage return. */\n    public static int Value()\n    {\n        int total = 1;\n          return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Separated.cs"), $"interface ISeparated\n{{\n    int Value();\n}}\n\n/* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\n"
                + "class FirstSeparated : ISeparated\n{\n    public int Value() => 1;\n}\n\nclass SecondSeparated : ISeparated\n{\n    public int Value() => Separators.Value();\n}\n");
            File.WriteAllText(PathOf("Crlf.cs"), "static class Crlf\r\n{\r\n    public static int One()\r\n    {\r\n          return 1;\r\n    }\r\n}\r\n");
            // Each file under Breaks the server writes with the other line break than its own, on any system.
            Directory.CreateDirectory(PathOf("Breaks"));
            File.WriteAllText(PathOf("Breaks/.editorconfig"), "root = true\n\n[LfFile.cs]\nend_of_line = crlf\n\n[CrlfFile.cs]\nend_of_line = lf\n");
            File.WriteAllText(PathOf("Breaks/LfFile.cs"), "class LfFile\n{\n    public int X { get; }\n\n    static void Make()\n    {\n        int made = 0;\n    }\n}\n");
            File.WriteAllText(PathOf("Breaks/CrlfFile.cs"), "class CrlfFile\r\n{\r\n    public int X { get; }\r\n\r\n    static void Make()\r\n    {\r\n        int made = 0;\r\n    }\r\n}\r\n");
            // Tabs: an .editorconfig asks for tabs. Detected: no .editorconfig says, and each file's own lines do.
            Directory.CreateDirectory(PathOf("Tabs"));
            File.WriteAllText(PathOf("Tabs/.editorconfig"), "[*.cs]\nindent_style = tab\n");
            File.WriteAllText(PathOf("Tabs/Tabbed.cs"), "class Tabbed\n{\n\tpublic int X { get; }\n\n\tstatic void Make()\n\t{\n\t\tint made = 0;\n\t}\n}\n");
            // Keys: an .editorconfig setting what Roslyn reads, and what nothing reads.
            Directory.CreateDirectory(PathOf("Keys"));
            File.WriteAllText(PathOf("Keys/.editorconfig"), "[*.cs]\ncsharp_style_var_elsewhere = true:warning\ndotnet_diagnostic.IDE0008.severity = suggestion\n"
                + "dotnet_diagnostic.ZZ9999.severity = warning\ncsharp_unread_option = true\ncsharp_space_after_cast = sometimes\nend_of_line = lfx\n");
            Directory.CreateDirectory(PathOf("Detected"));
            File.WriteAllText(PathOf("Detected/TwoSpaces.cs"), "class TwoSpaces\n{\n  public int X { get; }\n\n  static void Make()\n  {\n    int made = 0;\n    if (made > 0)\n    {\n      made--;\n    }\n  }\n}\n");
            File.WriteAllText(PathOf("Detected/Sixes.cs"), Sixes);
            File.WriteAllText(PathOf("Detected/TabsOwn.cs"), "class TabsOwn\n{\n\tpublic int X { get; }\n\n\tstatic void Make()\n\t{\n\t\tint made = 0;\n\t}\n}\n");
            File.WriteAllText(PathOf("Inferred.cs"), "using System;\nusing System.Collections.Generic;\n\nstatic class Inferred\n{\n    public static int Run()\n    {\n"
                + "        var list = new List<int>();\n        var total = Area.Of(3, 4);\n        Func<int, int> square = n => n * n;\n"
                + "        list.ForEach(item => total += square(item));\n        List<int> kept = new();\n        total += Count(new());\n"
                + "        var (first, second) = (total, \"two\");\n        return first + second.Length + kept.Count;\n    }\n\n"
                + "    private static int Count(List<int> values) => values.Count;\n}\n");
            // Tidy: laid out from the Explorer, one file open and one not.
            Directory.CreateDirectory(PathOf("Tidy"));
            File.WriteAllText(PathOf("Tidy/Crooked.cs"), Crooked);
            File.WriteAllText(PathOf("Tidy/Spaced.cs"), Spaced);
            File.WriteAllText(PathOf("Messy.cs"), "using System.Text;\nusing System;\nusing System.Collections.Generic;\n\nstatic class Messy\n{\n    public static int Count()\n    {\n          List<string> names = [];\n        Console.WriteLine(names.Count);\n      return names.Count;\n    }\n}\n");
            // Each one test's own, which no other file uses: written by the test, it cost the warm server before and after it.
            File.WriteAllText(PathOf("Pairs.cs"), "class Counter(int start)\n{\n    public int Start => start;\n}\n\nrecord Pair(int Left, int Right)\n{\n"
                + "    public static int Sum(Pair pair) => pair.Left + new Counter(pair.Right).Start;\n}\n");
            File.WriteAllText(PathOf("Greeters.cs"), "class Greeter(string name, int times)\n{\n    public string Name => name;\n\n    public int Times => times;\n\n"
                + "    public static Greeter Of(string name)\n    {\n        Greeter made = new(name, 2);\n        return made;\n    }\n}\n");
            File.WriteAllText(PathOf("Releases.cs"), "record Release(string Version, int Downloads);\n");
            File.WriteAllText(PathOf("ReleaseReaders.cs"), "static class ReleaseReaders\n{\n    public static string Of(Release release) => release.Version;\n\n"
                + "    public static Release Next(Release release) => release with { Version = \"2.0\" };\n}\n");
            File.WriteAllText(PathOf("Mixed.cs"), "static class Mixed\n{\n    /* Pasted:\u2028one,\r\ntwo,\u0085three. */\n    public static int Value()\n    {\n        int total = 1;\n          return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Halves.cs"), "static class Halves\n{\n    /* one,\r\ntwo,\r\nthree. */\n    public static int Value()\n    {\n        int total = 1;\n        return total;\n    }\n}\n");
            File.WriteAllText(PathOf("Average.cs"), "using System.Collections.Generic;\n\nstatic class Average\n{\n    public static double Of(IEnumerable<int> values)\n    {\n"
                + "        int total = 0;\n        int count = 0;\n        foreach (int value in values)\n        {\n            total += value;\n            count++;\n        }\n"
                + "        return total / count;\n    }\n}\n");
            File.WriteAllText(PathOf("Casts.cs"), "static class Casts\n{\n    public static int Three() => (int)3;\n}\n");
            File.WriteAllText(PathOf("Kinds.cs"), KindsCode);
            File.WriteAllText(PathOf("Ragged.cs"), Ragged);
            File.WriteAllText(PathOf("Fits.cs"), FitsCode);
            File.WriteAllText(PathOf("Marks.cs"), "static class Marks\n{\n    public static int Greet()\n    {\n        int नमस्ते = 1;\n        int twice = ;\n        return ;\n    }\n}\n");
            File.WriteAllText(PathOf("Wide.cs"), "static class Wide\n{\n    public static int Four()\n    {\n        return Area.Of(2, 2);\n    }\n}\n", Encoding.Unicode);
            File.WriteAllText(PathOf("Actions.cs"), "static class Actions\n{\n    public static int Go()\n    {\n        int half = Half(4);\n"
                + "        Missing gone = null;\n        return half;\n    }\n\n    private static int Half(int value) => value / 2;\n}\n");
            // Documented as .NET documents String.Join(String, IEnumerable<String>): no space between the full stop and the paramref.
            File.WriteAllText(PathOf("Joins.cs"), "using System.Collections.Generic;\n\nstatic class Joins\n{\n"
                + "    /// <summary>Joins the values.</summary>\n"
                + "    /// <param name=\"separator\">The string to use as a separator.<paramref name=\"separator\"/> is put between values only.</param>\n"
                + "    /// <param name=\"values\">The values to join.</param>\n"
                + "    public static string Join(string separator, IEnumerable<string> values) => string.Join(separator, values);\n\n"
                + "    public static string Both(List<string> names) => Join(\", \", names) + string.Join(\", \", names);\n}\n");
            // Documentation naming a path in its words and as code, and quoting a word.
            File.WriteAllText(PathOf("Paths.cs"), "static class Paths\n{\n"
                + "    /// <summary>Keeps its logs under <c>C:\\temp</c>, in C:\\temp\\logs, &quot;never&quot; elsewhere.</summary>\n"
                + "    public static int Keep() => 1;\n}\n");
            // A statement's completion with no usings, and with the usings a project's implicit ones bring.
            const string Statement = "\nstatic class Statement\n{\n    public static int Sum()\n    {\n        int total = 0;\n        return total;\n    }\n}\n";
            File.WriteAllText(PathOf("Bare.cs"), Statement.Replace("Statement", "Bare", StringComparison.Ordinal));
            File.WriteAllText(PathOf("Used.cs"), "using System;\nusing System.Collections.Generic;\nusing System.IO;\nusing System.Linq;\nusing System.Net.Http;\n"
                + "using System.Threading;\nusing System.Threading.Tasks;\n" + Statement.Replace("Statement", "Used", StringComparison.Ordinal));
            // Two classes alike but for how many members they have, every one named alike, fewer than the server's thousand.
            File.WriteAllText(PathOf("Pantry.cs"), Rations("Pantry", 220));
            File.WriteAllText(PathOf("Granary.cs"), Rations("Granary", 980));
            // A class of thousands of fields, whose statements list far more than the server's thousand.
            File.WriteAllText(PathOf("Silo.cs"), Rations("Silo", 4000));
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }

        /// <summary>A class of <paramref name="count"/> fields named Ration000 on, and a method to type its name into.</summary>
        private static string Rations(string type, int count) =>
            $"static class {type}\n{{\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"    public static int Ration{i:000};\n"))
            + "\n    public static int Tally()\n    {\n        int total = 0;\n        return total;\n    }\n}\n";

        /// <summary>Whether restore downloaded .NET's reference pack, as it does when no installed SDK has the version the
        /// newest SDK targets for net10.0: the package has no IntelliSense files beside its assemblies, which the SDK's own
        /// copy has, so no documentation shows for .NET's types.</summary>
        public bool ReferencePackDownloaded =>
            File.ReadAllText(PathOf("obj/project.assets.json")).Contains("\"name\": \"Microsoft.NETCore.App.Ref\"", StringComparison.Ordinal);
    }
}

/// <summary>What reading the language server's completion costs, counted on every thread: the class runs alone, as whatever a
/// class beside the window tests allocates meanwhile would count as the read's.</summary>
[Collection(Alone.Name)]
public sealed class CodeAssistBytesTests : IClassFixture<CodeAssistTests.Project>
{
    private readonly CodeAssistTests.Project project;

    public CodeAssistBytesTests(CodeAssistTests.Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>C#'s longest answers: a completion list costs the UI thread little of its reading, and ten thousand usages
    /// and four thousand implementations are listed whole.</summary>
    [AvaloniaFact]
    public void Csharps_longest_answers_are_read_off_the_UI_thread_and_listed_whole()
    {
        Flow.StepApart("a statement's completion list is read off the UI thread", () => new CodeAssistBytesTests(project),
            t => t.A_statements_completion_list_is_read_off_the_UI_thread());
        Flow.StepApart("Find Usages lists ten thousand usages and Derived Symbols four thousand implementations", () => new CodeAssistResponsivenessTests(project),
            t => t.Find_usages_lists_ten_thousand_usages_and_derived_symbols_four_thousand_implementations());
    }

    /// <summary>The server's completion list is read where its answer arrives, never on the UI thread: a list of
    /// hundreds more items (a file with the usings a project's implicit ones bring, against one with none) costs the UI
    /// thread, from the key to the list open, less than a third of what reading those items takes, counted rather than
    /// timed. The window starts a server of its own: a warm server, once a test had it index the types of namespaces not
    /// imported, lists them whole in both files, which leaves the two lists near enough alike.</summary>
    private void A_statements_completion_list_is_read_off_the_UI_thread()
    {
        string bare = project.PathOf("Bare.cs");
        string used = project.PathOf("Used.cs");
        MainWindow window = Ui.OpenFolder(project.Folder, autosaves: false);
        window.OpenFile(bare);
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        (int fewItems, long fewRead, long fewFrame) = StatementCompletion(window, bare);
        (int manyItems, long manyRead, long manyFrame) = StatementCompletion(window, used);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{fewItems} items: read in {fewRead / 1024} KB, opened from the key in {fewFrame / 1024} KB; {manyItems} items: {manyRead / 1024} KB, {manyFrame / 1024} KB");
        Assert.True(manyItems > fewItems + 300, $"With the usings a statement's completion listed {manyItems} items, against {fewItems} without");
        Assert.True(manyFrame - fewFrame < (manyRead - fewRead) / 3,
            $"A list of {manyItems} items opened with {manyFrame / 1024} KB allocated on the UI thread from the key, against {fewFrame / 1024} KB for {fewItems} items: "
            + $"a third or more of the {(manyRead - fewRead) / 1024} KB reading the extra items takes, so they were read there");
        window.Close();
    }

    /// <summary>A statement's completion in a file: how many items it lists, what reading its answer allocates (the
    /// same list asked for from the thread pool, the window's thread waiting), and what the UI thread allocates from the
    /// key until the list is open, whichever of its frames the answer lands in; the least of three. Its later part is
    /// held in each round until Escape: one joined before it leaves the popup, for the next round, rows to lay out again
    /// (200 for 54), which the UI thread pays as though it read the items.</summary>
    private static (int Items, long Read, long Frame) StatementCompletion(MainWindow window, string path)
    {
        window.OpenFile(path);
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "        int total = 0;");
        window.KeyTextInput(" ");
        editor.Focus();
        Ui.Settle();
        Passenger.Lsp.LspDocument lsp = window.CSharpService!.For(window.EditorArea.Active!)!.Lsp!;
        int caret = editor.Selections.Primary.Caret;
        long read = long.MaxValue;
        int items = 0;
        for (int i = 0; i < 3; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            Passenger.Lsp.CompletionList? list = Ui.Idle(Waiting.Apart(() => lsp.CompletionAsync(caret, null)), "the statement's completion asked from the thread pool").List;
            read = Math.Min(read, GC.GetTotalAllocatedBytes(precise: true) - before);
            items = list?.Items.Count ?? 0;
            if (list?.Later is { } later)
            {
                // Asked for as the list came: landed, it is no answer a round below would hold in place of its own.
                Ui.Await(later, "the later part of the statement's completion asked from the thread pool");
            }
        }
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        long frame = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task held = server.HoldNextAnswer(Passenger.Roslyn.Extension.ExtensionMethods.LaterCompletion, _ => true, released.Task);
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.KeyTextInput("S");
                Ui.Pumped(() => window.Assist.IsCompletionOpen, TimeSpan.FromSeconds(60));
                frame = Math.Min(frame, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.True(window.Assist.IsCompletionOpen, $"Completion never opened for a statement in {Path.GetFileName(path)}");
                Ui.AwaitPumped(held, $"the later part of a statement's completion in {Path.GetFileName(path)}, come and held unanswered");
                Ui.Press(window, "ESCAPE");
                Ui.Press(window, "BACK_SPACE");
            }
            finally
            {
                released.TrySetResult();
            }
        }
        return (items, read, frame);
    }
}

/// <summary>The language server's longest lists, on the code help's project with thousands of usages and implementations
/// written into it: a step of <see cref="CodeAssistBytesTests"/>' flow.</summary>
public sealed class CodeAssistResponsivenessTests
{
    private readonly CodeAssistTests.Project project;

    public CodeAssistResponsivenessTests(CodeAssistTests.Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>Find Usages lists ten thousand usages and Derived Symbols four thousand implementations, each with its line
    /// of code read from its closed file. Both lists' files are written at once, so one server loads them.</summary>
    internal void Find_usages_lists_ten_thousand_usages_and_derived_symbols_four_thousand_implementations()
    {
        for (int file = 0; file < 4; file++)
        {
            StringBuilder text = new($"static class Uses{file}\n{{\n    public static int All()\n    {{\n        int total = 0;\n");
            for (int call = 0; call < 2500; call++)
            {
                text.Append("        total += Helper.Twice(").Append(call).Append(");\n");
            }
            project.Write($"Uses{file}.cs", text.Append("        return total;\n    }\n}\n").ToString());
        }
        project.Write("Notes.txt", string.Empty);
        project.Write("Measure.cs", "interface IMeasure\n{\n    int Size();\n}\n");
        for (int file = 0; file < 4; file++)
        {
            StringBuilder text = new();
            for (int type = 0; type < 1000; type++)
            {
                text.Append("sealed class Measure").Append(file).Append('_').Append(type).Append(" : IMeasure\n{\n    public int Size() => ").Append(type).Append(";\n}\n");
            }
            project.Write($"Measures{file}.cs", text.ToString());
        }
        MainWindow window = project.Open("Helper.cs");
        window.OpenFile(project.PathOf("Notes.txt"));
        window.OpenFile(project.PathOf("Helper.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Twi");
        // The server has the folder loaded before the window is timed, which is the server's work, not the window's.
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, "the language server");
        Ui.Settle();
        FindView find = window.Find;
        Ui.Run(window, AppActions.FindUsages);
        Ui.PumpUntil(() => find.Shown == "Usages of Twice" && find.Rows.Count(r => r.Usage is not null) == 10_002, "the usages in the Find window", TimeSpan.FromMinutes(2));

        window.OpenFile(project.PathOf("Measure.cs"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "int Si");
        Ui.Settle();
        Ui.Run(window, AppActions.DerivedSymbols);
        Ui.PumpUntil(() => window.Assist.Offered.Count == 4000, "the four thousand implementations to choose from", TimeSpan.FromMinutes(2));
        Ui.Press(window, "ESCAPE");
        window.Close();
    }
}
