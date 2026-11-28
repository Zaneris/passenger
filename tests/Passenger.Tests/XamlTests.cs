using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Avalonia XAML through the window, on an app made by <c>dotnet new avalonia.app</c> with a view model, a theme
/// palette and an asset of its own, and on Passenger's own XAML: what completion offers as a file is typed, read by
/// Passenger's own XAML from the project's referenced assemblies and its C#.</summary>
public sealed class XamlTests : IClassFixture<XamlTests.Gallery>
{
    private readonly Gallery app;

    public XamlTests(Gallery app)
    {
        this.app = app;
        app.Reset();
    }

    /// <summary>The gate's XAML completion, checks, renames, assets, colours and preview, as one flow.</summary>
    [Gate]
    [AvaloniaFact]
    public void The_gates_xaml_completion_checks_renames_assets_colours_and_preview_hold_as_one_flow()
    {
        Flow.StepApart("typing a tag offers the controls, then a control's properties, then the values of the one chosen", () => new XamlTests(app), t => t.Typing_a_tag_offers_the_controls_then_a_controls_properties_and_then_the_values_of_the_one_chosen());
        Flow.StepApart("a value chosen with nothing typed from the list its property opened leaves the caret after it to type on", () => new XamlTests(app), t => t.A_value_chosen_with_nothing_typed_from_the_list_its_property_opened_leaves_the_caret_after_it_to_type_on());
        Flow.StepApart("a misspelt property, enum value, resource and binding path are marked as typed, listed in Problems and go once put right", () => new XamlTests(app), t => t.A_misspelt_property_enum_value_resource_and_binding_path_are_marked_as_typed_listed_in_problems_and_go_once_put_right());
        Flow.StepApart("Passenger's own XAML, which compiles, is marked with no problem", () => new XamlTests(app), t => t.Passengers_own_xaml_which_compiles_is_marked_with_no_problem());
        Flow.StepApart("an element's name renames with its code-behind's uses, and Go to Declaration from the code lands on it", () => new XamlTests(app), t => t.An_elements_name_renames_with_its_code_behinds_uses_and_go_to_declaration_from_the_code_lands_on_it());
        Flow.StepApart("an avares path completes from the project's assets, is warned of when missing and goes to its file", () => new XamlTests(app), t => t.An_avares_path_completes_from_the_projects_assets_is_warned_of_when_missing_and_goes_to_its_file());
        Flow.StepApart("colours show as gutter swatches, and one chosen in the picker is written back as it was written", () => new XamlTests(app), t => t.Colours_show_as_gutter_swatches_and_one_chosen_in_the_picker_is_written_back_as_it_was_written());
        Flow.StepApart("a window closed as its preview waits for the previewer is let go", () => new XamlTests(app), t => t.A_window_closed_as_its_preview_waits_for_the_previewer_is_let_go());
    }

    /// <summary>XAML completed and checked as its compiler reads it: a resource's keys and a binding's path; Enter between
    /// a property's empty quotes writing its element where an event's breaks the line; an x:Class no C# declares marked;
    /// the compiler's error after a build at the editor's line below a lone carriage return; and markup nested past a
    /// stack, a type its own base or a cycle, or half a surrogate pair leaving the XAML checked and the IDE running.</summary>
    [AvaloniaFact]
    public void Xaml_is_completed_and_checked_as_its_compiler_reads_it_and_no_text_ends_the_ide()
    {
        Flow.StepApart("a resource reference offers the keys the app merges in, and a binding its data type's properties through a path", () => new XamlTests(app), t => t.A_resource_reference_offers_the_keys_the_app_merges_in_and_a_binding_its_data_types_properties_through_a_path());
        Flow.StepApart("Enter between a property's empty quotes writes its element, and between an event's, a directive's or an unknown element's breaks the line", () => new XamlTests(app), t => t.Enter_between_a_propertys_empty_quotes_writes_its_element_and_between_an_events_a_directives_or_an_unknown_elements_breaks_the_line());
        Flow.StepApart("an x:Class no C# declares is marked as Avalonia's compiler refuses it", () => new XamlTests(app), t => t.An_x_class_no_csharp_declares_is_marked_as_avalonias_compiler_refuses_it());
        Flow.StepApart("after a build, the XAML compiler's error below a lone carriage return is listed, marked and gone to at the editor's line", () => new XamlTests(app), t => t.After_a_build_the_xaml_compilers_error_below_a_lone_carriage_return_is_listed_marked_and_gone_to_at_the_editors_line());
        Flow.StepApart("a value of markup extensions nested a hundred thousand deep leaves the XAML checked and the IDE running", () => new XamlTests(app), t => t.A_value_of_markup_extensions_nested_a_hundred_thousand_deep_leaves_the_xaml_checked_and_the_ide_running());
        Flow.StepApart("an interface typed as its own base or in a cycle beside open XAML leaves the XAML checked and the IDE running", () => new XamlTests(app), t => t.An_interface_typed_as_its_own_base_or_in_a_cycle_beside_open_xaml_leaves_the_xaml_checked_and_the_ide_running());
        Flow.StepApart("a character reference to half a surrogate pair leaves its file and the project's others with their help", () => new XamlTests(app), t => t.A_character_reference_to_half_a_surrogate_pair_leaves_its_file_and_the_projects_others_with_their_help());
    }

    /// <summary>Compiled bindings: one with no data type marked, the fix giving the view its view model's type clearing
    /// it and building; a binding element, a data context, a relative source and a cast path marked until given one; an
    /// element's own data context binding starting from the type above it; and one under items starting from their
    /// type.</summary>
    [AvaloniaFact]
    public void A_compiled_binding_starts_from_the_type_its_data_context_gives_and_is_marked_until_given_one()
    {
        Flow.StepApart("a compiled binding with no data type is marked, and the fix giving the view its view model's type clears it and builds", () => new XamlTests(app), t => t.A_compiled_binding_with_no_data_type_is_marked_and_the_fix_giving_the_view_its_view_models_type_clears_it_and_builds());
        Flow.StepApart("a binding element, a data context relative source and a cast path with no data type are marked, and build once given one", () => new XamlTests(app), t => t.A_binding_element_a_data_context_relative_source_and_a_cast_path_with_no_data_type_are_marked_and_build_once_given_one());
        Flow.StepApart("an element's own data context binding starts from the type above it and gives its path's type to what is inside", () => new XamlTests(app), t => t.An_elements_own_data_context_binding_starts_from_the_type_above_it_and_gives_its_path_type_to_what_is_inside());
        Flow.StepApart("a compiled binding under a data context of no type is marked, and one under items starts from their type", () => new XamlTests(app), t => t.A_compiled_binding_under_a_data_context_of_no_type_is_marked_and_one_under_items_starts_from_their_type());
    }

    /// <summary>XAML gets its help as what it reads arrives: a view model's property added in a referenced project, a
    /// project that takes Avalonia or a package restored while the XAML is open, one restored into artifacts, and a
    /// project holding folder links back up its own tree.</summary>
    [AvaloniaFact]
    public void Xaml_gets_its_help_as_projects_and_packages_arrive_and_change()
    {
        Flow.StepApart("a property added to a view model in a referenced project reaches the XAML that binds to it", () => new XamlTests(app), t => t.A_property_added_to_a_view_model_in_a_referenced_project_reaches_the_xaml_that_binds_to_it());
        Flow.StepApart("a project that takes Avalonia while its XAML is open gets its XAML help once the restore lands", () => new XamlTests(app), t => t.A_project_that_takes_avalonia_while_its_xaml_is_open_gets_its_xaml_help_once_the_restore_lands());
        Flow.StepApart("a package restored while its XAML is open has its types read once the restore lands", () => new XamlTests(app), t => t.A_package_restored_while_its_xaml_is_open_has_its_types_read_once_the_restore_lands());
        Flow.StepApart("a package restored into artifacts while its XAML is open has its types read once the restore lands", () => new XamlTests(app), t => t.A_package_restored_into_artifacts_while_its_xaml_is_open_has_its_types_read_once_the_restore_lands(false));
        Flow.StepApart("a project holding folder links back up its own tree gets its XAML help", () => new XamlTests(app), t => t.A_project_holding_folder_links_back_up_its_own_tree_gets_its_xaml_help());
    }

    /// <summary>XAML and the C# it names: Go to Declaration from XAML; a bound property and a handler with their XAML
    /// among their usages, however the path reaches them, and Safe Delete naming it; renames from the C#, of an
    /// element's field from the code-behind and of an element's name, refused where the name would break the code,
    /// keeping a line written meanwhile and waiting for the server to read a XAML file just written or saved.</summary>
    [AvaloniaFact]
    public void Xaml_is_found_among_usages_and_renamed_with_the_csharp_it_names_waiting_for_the_server_to_read_it()
    {
        Flow.StepApart("Go to Declaration from XAML opens a control's class, a resource's definition and a bound property", () => new XamlTests(app), t => t.Go_to_declaration_from_xaml_opens_a_controls_class_a_resources_definition_and_a_bound_property());
        Flow.StepApart("a bound property and a handler have their XAML among their usages, and Safe Delete names it before breaking the build", () => new XamlTests(app), t => t.A_bound_property_and_a_handler_have_their_xaml_among_their_usages_and_safe_delete_names_it_before_breaking_the_build());
        Flow.StepApart("a property bound by an element, its path text, a null-conditional path or after an indexer has that XAML among its usages", () => new XamlTests(app), t => t.A_property_bound_by_an_element_its_path_text_a_null_conditional_path_or_after_an_indexer_has_that_xaml_among_its_usages());
        Flow.StepApart("Find Usages of a property no XAML names reads no project's XAML, and of one bound reads it once", () => new XamlTests(app), t => t.Find_usages_of_a_property_no_xaml_names_reads_no_projects_xaml_and_of_one_bound_reads_it_once());
        Flow.StepApart("renaming a bound property, a command, a static member and a handler in C# renames the XAML using them, and the app builds", () => new XamlTests(app), t => t.Renaming_a_bound_property_a_command_a_static_member_and_a_handler_in_csharp_renames_the_xaml_using_them_and_the_app_builds());
        Flow.StepApart("an element's field renamed from the code-behind renames the element in its XAML and every use", () => new XamlTests(app), t => t.An_elements_field_renamed_from_the_code_behind_renames_the_element_in_its_xaml_and_every_use());
        Flow.StepApart("an element's name is not renamed to a C# keyword, another element's or its handler's, and its files are left as they were", () => new XamlTests(app), t => t.An_elements_name_is_not_renamed_to_a_csharp_keyword_another_elements_or_its_handlers_and_its_files_are_left_as_they_were());
        Flow.StepApart("a line written in the code-behind while an element's rename is out still renames its use there", () => new XamlTests(app), t => t.A_line_written_in_the_code_behind_while_an_elements_rename_is_out_still_renames_its_use_there());
        Flow.StepApart("an element's field renamed from the code-behind a moment after its XAML was written waits for the server to read it", () => new XamlTests(app), t => t.An_elements_field_renamed_from_the_code_behind_a_moment_after_its_xaml_was_written_waits_for_the_server_to_read_it());
        Flow.StepApart("a XAML file saved is kept for the language server to read before the folder's watcher tells of it", () => new XamlTests(app), t => t.A_xaml_file_saved_is_kept_for_the_language_server_to_read_before_the_folders_watcher_tells_of_it());
    }

    /// <summary>A theme's XAML: Go to Declaration reaching the theme's own XAML for a control, a template part and a
    /// resource; a key one theme variant lacks warned of where defined and used, the fix adding it to that variant; and
    /// an include climbing out of its sources' folder refused, nothing written outside it.</summary>
    [AvaloniaFact]
    public void A_themes_xaml_is_gone_to_and_fixed_by_variant_and_its_include_kept_inside_its_sources()
    {
        Flow.StepApart("Go to Declaration reaches the theme's own XAML for a control, a template part and a theme resource", () => new XamlTests(app), t => t.Go_to_declaration_reaches_the_themes_own_xaml_for_a_control_a_template_part_and_a_theme_resource());
        Flow.StepApart("a key one theme variant lacks is warned of where defined and used, and the fix adds it to that variant", () => new XamlTests(app), t => t.A_key_one_theme_variant_lacks_is_warned_of_where_defined_and_used_and_the_fix_adds_it_to_that_variant());
        Flow.StepApart("a theme's include that climbs out of its sources' folder is refused, and nothing is written outside it", () => new XamlTests(app), t => t.A_themes_include_that_climbs_out_of_its_sources_folder_is_refused_and_nothing_is_written_outside_it());
    }

    /// <summary>The Avalonia preview: a window drawn from the last build following its text as it is typed and styles
    /// as their PreviewWith; the caret's element outlined and a click selecting its start tag; a phone's and a tablet's
    /// size beside the design size; an Avalonia 11 app's previewer found in its packages with obj and bin moved; in the
    /// XAML workspace, what the caret's element sets itself; and a shared library's view drawn in its desktop head.</summary>
    [AvaloniaFact]
    public void The_avalonia_preview_draws_the_window_follows_the_caret_and_sizes_and_shows_what_an_element_sets()
    {
        Flow.StepApart("the preview draws a window from the last build, follows its text as it is typed and draws styles as their PreviewWith", () => new XamlTests(app), t => t.The_preview_draws_a_window_from_the_last_build_follows_its_text_as_it_is_typed_and_draws_styles_as_their_PreviewWith());
        Flow.StepApart("an Avalonia preview outlines the caret's element, and a click on one selects its start tag", () => new XamlTests(app), t => t.An_avalonia_preview_outlines_the_carets_element_and_a_click_on_one_selects_its_start_tag());
        Flow.StepApart("an Avalonia preview draws its root at a phone's and a tablet's size beside its design size", () => new XamlTests(app), t => t.An_avalonia_preview_draws_its_root_at_a_phones_and_a_tablets_size_beside_its_design_size());
        Flow.StepApart("an app on Avalonia 11 with its obj and bin folders moved previews from its package's previewer", () => new XamlTests(app), t => t.An_app_on_Avalonia_11_with_its_obj_and_bin_folders_moved_previews_from_its_packages_previewer());
        Flow.StepApart("in the XAML workspace the preview shows what the caret's element sets itself while no app runs", () => new XamlTests(app), t => t.In_the_xaml_workspace_the_preview_shows_what_the_carets_element_sets_itself_while_no_app_runs());
        Flow.Step("a shared library's view previews in the desktop head that runs it, once Build for XAML Preview built that head", XamlLibraryPreviewTests.A_shared_librarys_view_previews_in_the_desktop_head_that_runs_it);
    }

    /// <summary>An Avalonia previewer's life: sent nothing until its session started; an edit's preview settling on its
    /// own frame; one run for the preview shown and the one hidden last, a build starting only the shown one again; its
    /// end said with its last line or exit, the next edit drawing in a new one; a failed XAML compile's rebuild
    /// offered; and twenty opens and closes beside busy processes faulting no read.</summary>
    [AvaloniaFact]
    public void A_previewers_life_is_followed_from_its_session_to_its_end_and_never_holds_the_window()
    {
        Flow.StepApart("a previewer is sent nothing until it says its session started", () => new XamlTests(app), t => t.A_previewer_is_sent_nothing_until_it_says_its_session_started());
        Flow.StepApart("an edit's preview settles on its own frame, never one of the window its load replaces", () => new XamlTests(app), t => t.An_edits_preview_settles_on_its_own_frame_never_one_of_the_window_its_load_replaces());
        Flow.StepApart("previewers run for the preview shown and the one hidden last, and a build starts only the shown one again", () => new XamlTests(app), t => t.Previewers_run_for_the_preview_shown_and_the_one_hidden_last_and_a_build_starts_only_the_shown_one_again());
        Flow.StepApart("a previewer that writes a last line and exits at once is said to have ended with that line", () => new XamlTests(app), t => t.A_previewer_that_writes_a_last_line_and_exits_at_once_is_said_to_have_ended_with_that_line());
        Flow.StepApart("an Avalonia previewer that ends says its exit, and the next edit draws in a new one", () => new XamlTests(app), t => t.An_Avalonia_previewer_that_ends_says_its_exit_and_the_next_edit_draws_in_a_new_one());
        Flow.StepApart("a build after a failed XAML compile says its XAML was never compiled, and the preview offers the rebuild", () => new XamlTests(app), t => t.A_build_after_a_failed_xaml_compile_says_its_xaml_was_never_compiled_and_the_preview_offers_the_rebuild());
        Flow.StepApart("opening and closing the XAML preview twenty times beside busy processes faults no read of the previewer", () => new XamlTests(app), t => t.Opening_and_closing_the_XAML_preview_twenty_times_beside_busy_processes_faults_no_read_of_the_previewer());
    }

    private void Typing_a_tag_offers_the_controls_then_a_controls_properties_and_then_the_values_of_the_one_chosen()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<'");
        Assert.Contains("Border", window.Assist.CompletionLabels);
        window.KeyTextInput("Butt");
        Ui.Settle();
        Assert.Equal("Button", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button|", CaretLine(editor), StringComparison.Ordinal);

        window.KeyTextInput(" ");
        window.KeyTextInput("C");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Click"), "a Button's events among its attributes");
        Assert.Contains("Command", window.Assist.CompletionLabels);
        Assert.Contains("Canvas.Left", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        window.KeyTextInput("H");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HorizontalAlignment"), "a Button's properties, its bases' included");
        window.KeyTextInput("orizontalA");
        Ui.Settle();
        Assert.Equal("HorizontalAlignment", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button HorizontalAlignment=\"|\"", CaretLine(editor), StringComparison.Ordinal);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Center"), "the enum's members offered inside the new value's quotes");
        Assert.Equal(["Center", "Left", "Right", "Stretch"], window.Assist.CompletionLabels.Order());
        window.KeyTextInput("Ce");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Button HorizontalAlignment=\"Center|\"", CaretLine(editor), StringComparison.Ordinal);
        window.Close();
    }

    private void A_value_chosen_with_nothing_typed_from_the_list_its_property_opened_leaves_the_caret_after_it_to_type_on()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<'");
        window.KeyTextInput("Butt");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        window.KeyTextInput("V");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("VerticalAlignment"), "a Button's properties");
        window.KeyTextInput("erticalA");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Center"), "the enum's members offered inside the new value's quotes");
        Assert.EndsWith("<Button VerticalAlignment=\"|\"", CaretLine(editor), StringComparison.Ordinal);

        // Chosen with nothing typed: the caret goes after the value, so the quote typed next steps over the closing one.
        string first = window.Assist.CompletionLabels[0];
        Ui.Press(window, "ENTER");
        Assert.True(CaretLine(editor).EndsWith($"<Button VerticalAlignment=\"{first}|\"", StringComparison.Ordinal),
            $"The value chosen with nothing typed left the caret before it, where typing on goes in front of it: {CaretLine(editor)}");
        window.KeyTextInput("\"");
        window.KeyTextInput(" ");
        window.KeyTextInput("H");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("HorizontalAlignment"), "a Button's properties after the value");
        Assert.EndsWith($"<Button VerticalAlignment=\"{first}\" H|", CaretLine(editor), StringComparison.Ordinal);
        window.Close();
    }

    private void A_resource_reference_offers_the_keys_the_app_merges_in_and_a_binding_its_data_types_properties_through_a_path()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Button Name=\"Go\" Content=\"Go\"");
        window.KeyTextInput(" Foreground=");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Red"), "the named colours a brush takes");
        window.KeyTextInput("{");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("StaticResource"), "the markup extensions after a value's brace");
        Assert.Contains("Binding", window.Assist.CompletionLabels);
        Assert.Contains("x:Static", window.Assist.CompletionLabels);
        window.KeyTextInput("StaticRes");
        Ui.Settle();
        Ui.Press(window, "ENTER");
        window.KeyTextInput(" ");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Accent"), "the palette's keys, merged in by App.axaml");
        Assert.Contains("Brand", window.Assist.CompletionLabels);
        Assert.Contains("Gallery.Gap", window.Assist.CompletionLabels);

        Ui.PlaceAfter(editor, "Text=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Greeting"), "the view model's properties, x:DataType's type's");
        Assert.Contains("Owner", window.Assist.CompletionLabels);
        Assert.Contains("Mode=", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        int greeting = editor.Document.Snapshot.ToString().IndexOf("{Binding Greeting}", StringComparison.Ordinal) + "{Binding ".Length;
        editor.ApplyEdits([new TextEdit(greeting, "Greeting".Length, "Owner.")]);
        editor.Selections = SelectionSet.Single(greeting + "Owner.".Length);
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Name"), "the path's next step's properties, Person's after Owner.");
        Assert.Contains("Age", window.Assist.CompletionLabels);
        Assert.DoesNotContain("Greeting", window.Assist.CompletionLabels);
        window.Close();
    }

    /// <summary>Enter between the empty quotes of an attribute the project's types read as a property's writes it as a
    /// property element, the element's first child with the caret inside, one step of Undo, an attached property's in an
    /// element it opens; an event's (<c>Click</c>) sets no property, and neither does a directive's or one of an element
    /// whose type nothing names, so Enter there breaks the line as anywhere.</summary>
    private void Enter_between_a_propertys_empty_quotes_writes_its_element_and_between_an_events_a_directives_or_an_unknown_elements_breaks_the_line()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument view = window.EditorArea.Active!;
        string written = editor.Document.Snapshot.ToString();
        const string Last = "<TextBlock Text=\"{Binding #Go.Content}\" />\n";
        string before = written.Replace("<StackPanel Margin=\"12\" Spacing=\"8\">", "<StackPanel Background=\"\" Margin=\"12\" Spacing=\"8\">", StringComparison.Ordinal)
            .Replace(Last, Last + "        <Button Click=\"\" Grid.Row=\"\" />\n        <TextBlock x:Name=\"\" />\n        <Mystery Width=\"\" />\n", StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(0, written.Length, before)]);
        Ui.WaitFor(() => window.XamlService.For(view)?.IsLoaded == true, "the project's XAML read");

        Ui.PlaceAfter(editor, "<StackPanel Background=\"");
        Ui.Press(window, "ENTER");
        string backed = before.Replace("<StackPanel Background=\"\" Margin=\"12\" Spacing=\"8\">\n",
            "<StackPanel Margin=\"12\" Spacing=\"8\">\n        <StackPanel.Background></StackPanel.Background>\n", StringComparison.Ordinal);
        Assert.True(Text(editor) == backed, $"Enter between Background's empty quotes did not write <StackPanel.Background> as the panel's first child:\n{Text(editor)}");
        Assert.Equal("        <StackPanel.Background>|</StackPanel.Background>", CaretLine(editor));
        Ui.Run(window, EditorActions.Undo);
        Assert.True(Text(editor) == before, $"One Undo did not take back Enter and the property element it wrote:\n{Text(editor)}");

        Ui.PlaceAfter(editor, "<Button Click=\"\" Grid.Row=\"");
        Ui.Press(window, "ENTER");
        Assert.True(Text(editor).Contains("        <Button Click=\"\">\n            <Grid.Row></Grid.Row>\n        </Button>\n", StringComparison.Ordinal),
            $"Enter between an attached property's empty quotes did not open the button and write <Grid.Row> inside it:\n{Text(editor)}");

        Ui.PlaceAfter(editor, "<Button Click=\"");
        Ui.Press(window, "ENTER");
        Assert.True(!Text(editor).Contains("Button.Click", StringComparison.Ordinal) && Text(editor).Contains("<Button Click=\"\n", StringComparison.Ordinal),
            $"Enter between an event's empty quotes wrote it as a property element, which XAML has none of for an event:\n{Text(editor)}");
        Ui.PlaceAfter(editor, "<TextBlock x:Name=\"");
        Ui.Press(window, "ENTER");
        Ui.PlaceAfter(editor, "<Mystery Width=\"");
        Ui.Press(window, "ENTER");
        Assert.True(Text(editor).Contains("<TextBlock x:Name=\"\n", StringComparison.Ordinal) && Text(editor).Contains("<Mystery Width=\"\n", StringComparison.Ordinal)
            && !Text(editor).Contains("x:Name>", StringComparison.Ordinal) && !Text(editor).Contains("Mystery.Width", StringComparison.Ordinal),
            $"Enter between a directive's or an unknown element's empty quotes did more than break the line:\n{Text(editor)}");
        window.Close();
    }

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    private void A_misspelt_property_enum_value_resource_and_binding_path_are_marked_as_typed_listed_in_problems_and_go_once_put_right()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Assert.Empty(document.Markers);
        Ui.PlaceAfter(editor, "<Button Name=\"Go\" Content=\"Go\"");
        window.KeyTextInput(" Foregrund=\"Red\" HorizontalAlignment=\"Centre\"");
        Replace(editor, "{Binding Greeting}", "{Binding Greting}");
        Replace(editor, "{StaticResource Accent}", "{StaticResource Acent}");
        Ui.WaitFor(() => document.Markers.Count(m => m.Kind == MarkerKind.Error) == 4, "the four mistakes marked as errors once typing paused");
        Assert.Equal("Cannot resolve property 'Foregrund' on Button", MarkerOn(editor, document, "Foregrund").Message);
        Assert.Equal("'Centre' is not a value of HorizontalAlignment", MarkerOn(editor, document, "Centre").Message);
        Assert.Equal("Cannot resolve property 'Greting' on MainViewModel", MarkerOn(editor, document, "Greting").Message);
        Assert.Equal("Resource 'Acent' is not found", MarkerOn(editor, document, "Acent").Message);
        Ui.WaitFor(() => window.BottomPanel.Problems.FileProblems.Count == 4, "Problems listing the file's four errors");
        Assert.All(window.BottomPanel.Problems.FileProblems, p => Assert.Equal(ProblemSeverity.Error, p.Severity));

        Replace(editor, "Foregrund", "Foreground");
        Replace(editor, "Greting", "Greeting");
        Ui.WaitFor(() => document.Markers.Count == 2, "the two put right no longer marked");
        Assert.DoesNotContain(document.Markers, m => m.Message.Contains("Foregrund", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>Markup extensions nested a hundred thousand deep in one value leave the file checked and the IDE running: a
    /// mistake beside them is marked and put right.</summary>
    private void A_value_of_markup_extensions_nested_a_hundred_thousand_deep_leaves_the_xaml_checked_and_the_ide_running()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        const int Depth = 100_000;
        string nested = string.Concat(Enumerable.Repeat("{Binding Path=", Depth)) + "Greeting" + new string('}', Depth);
        Replace(editor, "{Binding Greeting}", nested);
        Replace(editor, "Content=\"Go\"", "Contnt=\"Go\"");
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("Contnt", StringComparison.Ordinal)), "the misspelt property beside the nested value marked");
        Replace(editor, nested, "{Binding Greeting}");
        Replace(editor, "Contnt=\"Go\"", "Content=\"Go\"");
        Ui.WaitFor(() => document.Markers.Count == 0, "the file, put back as it was, no longer marked");
        window.Close();
    }

    private void An_interface_typed_as_its_own_base_or_in_a_cycle_beside_open_xaml_leaves_the_xaml_checked_and_the_ide_running()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<TextBlock Text=\"{Binding #Go.Content}\" />");
        window.KeyTextInput("\n        <TextBlock Text=\"{Binding Subtitle}\" />\n        <TextBlock Text=\"{Binding Caption}\" />");
        Ui.WaitFor(() => view.Markers.Count == 2, "the two properties the view model lacks marked");

        // On the way to IMainViewModelBase the interface names itself, which a binding the view model lacks then asks of.
        window.OpenFile(app.PathOf("ViewModels/MainViewModel.cs"));
        Ui.Settle();
        CodeEditor code = window.EditorArea.CodeEditor;
        Replace(code, "public class MainViewModel\n{", "public interface IMainViewModel : IMainViewModel\n{\n}\n\npublic class MainViewModel : IMainViewModel\n{\n    public string Caption { get; set; } = \"\";\n");
        Ui.WaitFor(() => view.Markers.Count == 1, "the property typed into the view model no longer marked, its types read again");
        Assert.Equal("Cannot resolve property 'Subtitle' on MainViewModel", Assert.Single(view.Markers).Message);

        // Two interfaces, each the other's base.
        Replace(code, "public interface IMainViewModel : IMainViewModel\n{\n}", "public interface IMainViewModel : IViewModelBase\n{\n}\n\npublic interface IViewModelBase : IMainViewModel\n{\n}");
        Replace(code, "public string Caption", "public string Heading");
        Ui.WaitFor(() => view.Markers.Count == 2, "the property renamed in the view model marked again, its types read again");
        Assert.Equal(["Cannot resolve property 'Caption' on MainViewModel", "Cannot resolve property 'Subtitle' on MainViewModel"], view.Markers.Select(m => m.Message).Order());

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Subt");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the binding's line");
        Assert.Equal("Create property 'Subtitle'", window.Assist.Offered[0]);
        window.Close();
    }

    private void A_character_reference_to_half_a_surrogate_pair_leaves_its_file_and_the_projects_others_with_their_help()
    {
        // An emoji written as two references, each half a UTF-16 pair: no character XML allows, read as written.
        string emoji = app.Write("Emoji.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.Emoji">
                <StackPanel>
                    <TextBlock Text="&#xD83D;&#xDE00;" />
                    <TextBlock Text="&#55357;" />
                </StackPanel>
            </UserControl>

            """);
        app.Write("Emoji.axaml.cs", "using Avalonia.Controls;\n\nnamespace Gallery;\n\npublic partial class Emoji : UserControl\n{\n}\n");
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument view = window.EditorArea.Active!;
        Ui.WaitFor(() => window.XamlService.For(view)?.IsLoaded == true, "the project's XAML read, the file with the references among it");
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        window.KeyTextInput("\n        <TextBlock Text=\"&#xD83D;\" />\n        ");
        window.KeyTextInput("<");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered beside a typed reference to half a pair");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");
        Ui.Settle();
        Assert.Empty(view.Markers);

        window.OpenFile(emoji);
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<TextBlock Text=\"&#55357;\"");
        window.KeyTextInput(" ");
        window.KeyTextInput("F");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Foreground"), "a TextBlock's properties in the file holding the references");
        window.Close();
    }

    private void A_property_added_to_a_view_model_in_a_referenced_project_reaches_the_xaml_that_binds_to_it()
    {
        // The usual layout: the app's XAML in Shop, its view models in Shop.Core, which Shop references.
        string folder = PathNames.Canonical(TestFolders.Create("xaml-core-"));
        string shop = Path.Combine(folder, "Shop");
        string core = Path.Combine(folder, "Shop.Core");
        string viewModel = Path.Combine(core, "ViewModels", "MainViewModel.cs");
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(shop, "Shop");
                string project = Path.Combine(shop, "Shop.csproj");
                File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
                    "  <ItemGroup>\n    <ProjectReference Include=\"..\\Shop.Core\\Shop.Core.csproj\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal));
                Directory.CreateDirectory(Path.GetDirectoryName(viewModel)!);
                File.WriteAllText(Path.Combine(core, "Shop.Core.csproj"),
                    TestProjects.Library("<Nullable>enable</Nullable>"));
                File.WriteAllText(viewModel, "namespace Shop.Core.ViewModels;\n\npublic class MainViewModel\n{\n    public string Greeting { get; set; } = \"Hello\";\n}\n");
                File.WriteAllText(Path.Combine(shop, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:vm="using:Shop.Core.ViewModels"
                            x:Class="Shop.MainWindow"
                            x:DataType="vm:MainViewModel"
                            Title="Shop">
                        <StackPanel>
                            <TextBlock Text="{Binding Greeting}" />
                            <TextBlock Text="{Binding Subtitle}" />
                            <TextBlock Text="{Binding Caption}" />
                        </StackPanel>
                    </Window>

                    """);
                Restore.Run(shop);
            }, folder);
            MainWindow window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(shop, "MainWindow.axaml"));
            OpenDocument view = window.EditorArea.Active!;
            Ui.WaitFor(() => view.Markers.Count == 2, "the two properties the referenced project's view model lacks marked");
            Assert.Equal(["Cannot resolve property 'Caption' on MainViewModel", "Cannot resolve property 'Subtitle' on MainViewModel"], view.Markers.Select(m => m.Message).Order());

            // Written on disk (a pull, another editor), and typed into the view model open in the editor.
            File.WriteAllText(viewModel, "namespace Shop.Core.ViewModels;\n\npublic class MainViewModel\n{\n    public string Greeting { get; set; } = \"Hello\";\n\n    public string Subtitle { get; set; } = \"\";\n}\n");
            Ui.WaitFor(() => view.Markers.Count == 1, "the property written into the referenced project's view model no longer marked");
            window.OpenFile(viewModel);
            Ui.Settle();
            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "public string Subtitle { get; set; } = \"\";");
            window.KeyTextInput("\n\n    public string Caption { get; set; } = \"\";");
            Ui.WaitFor(() => view.Markers.Count == 0, "the property typed into the referenced project's view model no longer marked");

            window.OpenFile(Path.Combine(shop, "MainWindow.axaml"));
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Text=\"{Binding ");
            Ui.Run(window, AppActions.Complete);
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Caption"), "the property typed into the referenced project offered in a binding");
            Assert.Contains("Subtitle", window.Assist.CompletionLabels);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A project whose folders link back up its own tree gets its XAML help, and Go to Declaration from a named
    /// element's field in the code-behind finds the XAML that declares its class, deep among those links.</summary>
    private void A_project_holding_folder_links_back_up_its_own_tree_gets_its_xaml_help()
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-links-"));
        string shop = Path.Combine(folder, "Shop");
        string views = Path.Combine(shop, "Views");
        string deep = Path.Combine(views, "Parts", "Deep");
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(shop, "Shop");
                Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(views, "Parts", "Palette.axaml"), Gallery.Palette);
                File.WriteAllText(Path.Combine(deep, "Card.axaml"), """
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 x:Class="Shop.Views.Card">
                        <Button Name="Pay" Content="Pay" />
                    </UserControl>

                    """);
                File.WriteAllText(Path.Combine(deep, "Card.axaml.cs"), """
                    using Avalonia.Controls;

                    namespace Shop.Views;

                    public partial class Card : UserControl
                    {
                        public Card()
                        {
                            InitializeComponent();
                            Pay.Content = "Pay now";
                        }
                    }

                    """);
                File.WriteAllText(Path.Combine(shop, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            x:Class="Shop.MainWindow"
                            Title="Shop">
                        <StackPanel>
                        </StackPanel>
                    </Window>

                    """);
                Restore.Run(shop);
            }, folder);
            // Links to the folder each is in and the one above it: a walk following them would double the folders each level.
            TestFolders.LinkFolder(Path.Combine(shop, "self"), shop);
            TestFolders.LinkFolder(Path.Combine(shop, "up"), folder);
            TestFolders.LinkFolder(Path.Combine(views, "here"), views);
            TestFolders.LinkFolder(Path.Combine(views, "Parts", "up"), views);

            MainWindow window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(shop, "MainWindow.axaml"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "<StackPanel>");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("<");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<', once the project's XAML files are read");
            Ui.Press(window, "ESCAPE");

            window.OpenFile(Path.Combine(deep, "Card.axaml.cs"));
            Ui.WaitFor(() => window.CSharpService?.IsReady == true, "the language server to load Shop");
            CodeEditor code = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(code, "        Pa");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "Card.axaml", "Go to Declaration of Pay to land in the XAML declaring Card, found among the links");
            Assert.Contains("<Button Name=\"|Pay\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
            Assert.Equal(PathNames.Canonical(Path.Combine(deep, "Card.axaml")), window.EditorArea.Active?.Path);
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void A_project_that_takes_avalonia_while_its_xaml_is_open_gets_its_xaml_help_once_the_restore_lands()
    {
        // A library with XAML in it but no Avalonia yet: nothing Passenger reads until the package is added and restored.
        string folder = PathNames.Canonical(TestFolders.Create("xaml-later-"));
        string project = Path.Combine(folder, "Later.csproj");
        const string Plain = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n";
        try
        {
            TestFolders.Prepare(() =>
            {
                File.WriteAllText(project, Plain + "</Project>\n");
                File.WriteAllText(Path.Combine(folder, "MainView.axaml"), """
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                        <StackPanel>
                            <TextBlock Text="Later" />
                        </StackPanel>
                    </UserControl>

                    """);
                Restore.Run(folder);
            }, folder);
            MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
            window.OpenFile(Path.Combine(folder, "MainView.axaml"));
            OpenDocument document = window.EditorArea.Active!;
            Passenger.App.Xaml.XamlDocument xaml = window.XamlService.For(document) ?? throw new InvalidOperationException("The XAML file of a C# project was given no XAML");
            Ui.WaitFor(() => xaml.Loaded.IsCompleted, "the project read and found to be neither Avalonia's nor WPF's");
            Assert.False(xaml.IsLoaded, "A project referencing neither Avalonia nor WPF had its XAML read as one of theirs");

            // Avalonia added, as NuGet's window or dotnet add package adds it, and restored.
            File.WriteAllText(project, $"{Plain}  <ItemGroup>\n    <PackageReference Include=\"Avalonia\" Version=\"{AvaloniaApp.Version}\" />\n  </ItemGroup>\n</Project>\n");
            Ui.Settle();
            // A tag begun before the restore lands takes its list from the project as it was, asked again once it is read.
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "<TextBlock Text=\"Later\" />");
            window.KeyTextInput("\n        ");
            window.KeyTextInput("<");
            Restore.Run(folder);
            Ui.WaitFor(() => xaml.IsLoaded, "the project's XAML read as Avalonia's once the package it now references was restored", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered, once the restore was read, after a tag's '<' typed before it landed");
            Ui.Press(window, "ESCAPE");
            Ui.Press(window, "BACK_SPACE");
            window.KeyTextInput("<");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Button"), "Avalonia's controls offered after a tag's '<' once the project references it");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void A_package_restored_while_its_xaml_is_open_has_its_types_read_once_the_restore_lands()
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-restore-"));
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(folder, "Fresh");
                File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:h="using:Avalonia.Headless"
                            x:Class="Fresh.MainWindow"
                            Title="Fresh">
                        <Window.Resources>
                            <h:AvaloniaHeadlessPlatformOptions x:Key="Headless" />
                        </Window.Resources>
                        <TextBlock Text="Welcome" />
                    </Window>

                    """);
                Restore.Run(folder);
            }, folder);
            MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
            window.OpenFile(Path.Combine(folder, "MainWindow.axaml"));
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("AvaloniaHeadlessPlatformOptions", StringComparison.Ordinal)),
                "the type of a package the app does not reference marked");

            // The package added where only its restore says so: the restore's assets are what the XAML reads it from.
            File.WriteAllText(Path.Combine(folder, "Directory.Build.props"),
                $"<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Avalonia.Headless\" Version=\"{AvaloniaApp.Version}\" />\n  </ItemGroup>\n</Project>\n");
            Ui.Settle();
            Restore.Run(folder);
            Ui.WaitFor(() => document.Markers.Count == 0, "the package's type resolved once its restore landed", TimeSpan.FromSeconds(30));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The same where the solution keeps its build output in artifacts/ (UseArtifactsOutput), its project in a folder
    /// below: the restore writes artifacts/obj/Fresh/project.assets.json, under no project's folder, on either way of
    /// watching (each folder on its own, as on Linux, or the whole tree).</summary>
    private void A_package_restored_into_artifacts_while_its_xaml_is_open_has_its_types_read_once_the_restore_lands(bool eachFolder)
    {
        string folder = PathNames.Canonical(TestFolders.Create("xaml-artifacts-"));
        string app = Path.Combine(folder, "src", "Fresh");
        const string Artifacts = "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n";
        try
        {
            TestFolders.Prepare(() =>
            {
                AvaloniaApp.Create(app, "Fresh");
                File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), Artifacts + "</Project>\n");
                File.WriteAllText(Path.Combine(app, "MainWindow.axaml"), """
                    <Window xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:h="using:Avalonia.Headless"
                            x:Class="Fresh.MainWindow"
                            Title="Fresh">
                        <Window.Resources>
                            <h:AvaloniaHeadlessPlatformOptions x:Key="Headless" />
                        </Window.Resources>
                        <TextBlock Text="Welcome" />
                    </Window>

                    """);
                Restore.Run(app);
            }, folder);
            Assert.True(File.Exists(Path.Combine(folder, "artifacts", "obj", "Fresh", "project.assets.json")), "The restore wrote its assets somewhere other than artifacts/obj/Fresh");
            MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, Watching = eachFolder ? FolderWatching.Inotify : FolderWatching.WholeTree };
            window.Show();
            window.OpenFolder(folder);
            window.OpenFile(Path.Combine(app, "MainWindow.axaml"));
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("AvaloniaHeadlessPlatformOptions", StringComparison.Ordinal)),
                "the type of a package the app does not reference marked");

            File.WriteAllText(Path.Combine(folder, "Directory.Build.props"),
                $"{Artifacts}  <ItemGroup>\n    <PackageReference Include=\"Avalonia.Headless\" Version=\"{AvaloniaApp.Version}\" />\n  </ItemGroup>\n</Project>\n");
            Ui.Settle();
            Restore.Run(app);
            Ui.WaitFor(() => document.Markers.Count == 0, "the package's type resolved once its restore landed in artifacts/", TimeSpan.FromSeconds(30));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    private void An_x_class_no_csharp_declares_is_marked_as_avalonias_compiler_refuses_it()
    {
        // Avalonia's compiler makes no class for it: it says "Unable to find type" (AVLN2000).
        string badge = app.Write("Badge.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.Badge">
                <TextBlock Text="New" />
            </UserControl>

            """);
        MainWindow window = app.Open(badge);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Count > 0, "the class nothing declares marked");
        Marker marked = MarkerOn(editor, document, "Gallery.Badge");
        Assert.Equal(MarkerKind.Error, marked.Kind);
        Assert.Equal("Cannot resolve class 'Gallery.Badge'", marked.Message);
        window.Close();
    }

    /// <summary>A view with no x:DataType in an Avalonia 12 project, which compiles its bindings: its {Binding} and its
    /// {CompiledBinding}, which Avalonia's compiler refuses (AVLN2100), are marked. The fix that gives the view the view
    /// model's x:DataType, its namespace declared, clears both as one step of Undo, and the project builds.</summary>
    private void A_compiled_binding_with_no_data_type_is_marked_and_the_fix_giving_the_view_its_view_models_type_clears_it_and_builds()
    {
        const string Unbound = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel>
                    <TextBlock Text="{Binding Greeting}" />
                    <TextBlock Text="{CompiledBinding Owner.Name}" />
                </StackPanel>
            </UserControl>

            """;
        const string NoDataType = "No x:DataType gives this compiled binding the type it binds to";
        MainWindow window = app.Open(app.Write("OwnerView.axaml", Unbound));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Count(m => m.Message == NoDataType) == 2,
            () => $"both bindings with no data type marked, as the build refuses them: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Assert.Equal(MarkerKind.Error, MarkerOn(editor, document, "Binding").Kind);
        Assert.Equal(MarkerKind.Error, MarkerOn(editor, document, "CompiledBinding").Kind);

        Ui.PlaceAfter(editor, "{Binding Gree");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the binding's line");
        Assert.Equal(["Add x:DataType 'MainViewModel' to UserControl", "Use ReflectionBinding"], window.Assist.Offered.Take(2));
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string typed = Unbound.Replace("xaml\">", "xaml\"\n             xmlns:vm=\"using:Gallery.ViewModels\"\n             x:DataType=\"vm:MainViewModel\">", StringComparison.Ordinal);
        Assert.Equal(typed, editor.Document.Snapshot.ToString());
        Ui.WaitFor(() => document.Markers.Count == 0, () => $"both bindings resolved once the view has its type: {string.Join("; ", document.Markers.Select(m => m.Message))}");

        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.True(editor.Document.Snapshot.ToString() == Unbound, $"One Undo did not take the whole fix back: {editor.Document.Snapshot}");
        Ui.WaitFor(() => document.Markers.Count(m => m.Message == NoDataType) == 2, "both bindings marked again once Undo took the type away");
        Ui.Run(window, EditorActions.Redo);
        Ui.Settle();
        Assert.Equal(typed, editor.Document.Snapshot.ToString());
        Ui.WaitFor(() => document.Markers.Count == 0, "both bindings resolved again after Redo");

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        // The gallery's window has a warning of its own (CS0108: its TextBlock named Title).
        string said = $"{Ui.Said(window)}: {string.Join("; ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code} {p.Where} {p.Message}"))}";
        Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The view given its type did not build: {said}");
        Assert.True(!window.BottomPanel.Problems.Problems.Any(p => p.Path?.EndsWith("OwnerView.axaml", StringComparison.Ordinal) == true), $"The build said something of the view: {said}");
        window.Close();
    }

    /// <summary>Three forms of compiled binding that start at the data context, each refused by Avalonia's compiler with no
    /// x:DataType (AVLN2100), are marked: a binding element, a binding to RelativeSource DataContext, and a path whose first
    /// step is cast. The binding element offers the view model's x:DataType, which clears all three; the view builds.</summary>
    private void A_binding_element_a_data_context_relative_source_and_a_cast_path_with_no_data_type_are_marked_and_build_once_given_one()
    {
        const string Unbound = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:Gallery.ViewModels">
                <StackPanel>
                    <TextBlock>
                        <TextBlock.Text>
                            <Binding Path="Greeting" />
                        </TextBlock.Text>
                    </TextBlock>
                    <ContentControl Content="{Binding RelativeSource={RelativeSource DataContext}}" />
                    <TextBlock Text="{Binding ((vm:Person)Owner).Name}" />
                </StackPanel>
            </UserControl>

            """;
        const string NoDataType = "No x:DataType gives this compiled binding the type it binds to";
        MainWindow window = app.Open(app.Write("FormsView.axaml", Unbound));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Count(m => m.Message == NoDataType) == 3,
            () => $"the three bindings with no data type marked, as the build refuses them: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        TextSnapshot snapshot = editor.Document.Snapshot;
        Assert.Equal([6, 9, 10], document.Markers.Select(m => snapshot.GetLineFromOffset(m.Range.Start)).Order());
        Assert.All(document.Markers, m => Assert.Equal(MarkerKind.Error, m.Kind));

        Ui.PlaceAfter(editor, "<Binding Pa");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the binding element's line");
        Assert.Equal(["Add x:DataType 'MainViewModel' to UserControl", "Use ReflectionBinding"], window.Assist.Offered.Take(2));
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string typed = Unbound.Replace("using:Gallery.ViewModels\">", "using:Gallery.ViewModels\"\n             x:DataType=\"vm:MainViewModel\">", StringComparison.Ordinal);
        Assert.Equal(typed, editor.Document.Snapshot.ToString());
        Ui.WaitFor(() => document.Markers.Count == 0, () => $"all three resolved once the view has its type: {string.Join("; ", document.Markers.Select(m => m.Message))}");

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string said = $"{Ui.Said(window)}: {string.Join("; ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code} {p.Where} {p.Message}"))}";
        Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The view given its type did not build: {said}");
        Assert.True(!window.BottomPanel.Problems.Problems.Any(p => p.Path?.EndsWith("FormsView.axaml", StringComparison.Ordinal) == true), $"The build said something of the view: {said}");
        window.Close();
    }

    /// <summary>An element's own DataContext binding resolves against the type above it, as Avalonia's compiler reads it,
    /// whatever x:DataType the element gives what is inside it; one with no x:DataType gives what is inside it its path's
    /// type. Each binding the build refuses is marked and none it takes, and completion and Go to Declaration in the
    /// DataContext binding take the type above, in a binding beside it the type it gives. Put right, the view builds.</summary>
    private void An_elements_own_data_context_binding_starts_from_the_type_above_it_and_gives_its_path_type_to_what_is_inside()
    {
        const string Contexts = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:Gallery.ViewModels"
                         x:DataType="vm:MainViewModel">
                <StackPanel>
                    <Border x:DataType="vm:Person" DataContext="{Binding Owner}">
                        <TextBlock Text="{Binding Name}" />
                    </Border>
                    <Border DataContext="{Binding Owner}" Tag="{Binding Age}">
                        <TextBlock Text="{Binding Name}" />
                    </Border>
                    <Border x:DataType="vm:Person" DataContext="{Binding Nickname}" />
                    <Border DataContext="{Binding Owner}">
                        <TextBlock Text="{Binding Greeting}" />
                    </Border>
                </StackPanel>
            </UserControl>

            """;
        MainWindow window = app.Open(app.Write("ContextsView.axaml", Contexts));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Count == 2, () => $"the two bindings the build refuses marked, and none it takes: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Assert.Equal("Cannot resolve property 'Nickname' on MainViewModel", MarkerOn(editor, document, "Nickname").Message);
        Assert.Equal("Cannot resolve property 'Greeting' on Person", MarkerOn(editor, document, "Greeting").Message);

        Ui.PlaceAfter(editor, "<Border x:DataType=\"vm:Person\" DataContext=\"{Binding ");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Owner"), "the DataContext binding completing the view model's properties, the type above the Border");
        Assert.Contains("Greeting", window.Assist.CompletionLabels);
        Assert.DoesNotContain("Age", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.PlaceAfter(editor, "<Border x:DataType=\"vm:Person\" DataContext=\"{Binding Own");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public Person |Owner", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.OpenFile(app.PathOf("ContextsView.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "Tag=\"{Binding Ag");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public int |Age", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("ContextsView.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        document = window.EditorArea.Active!;
        Replace(editor, "{Binding Nickname}", "{Binding Owner}");
        Replace(editor, "{Binding Greeting}", "{Binding Name}");
        Ui.WaitFor(() => document.Markers.Count == 0, () => $"every binding resolved once put right: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string said = $"{Ui.Said(window)}: {string.Join("; ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code} {p.Where} {p.Message}"))}";
        Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The view Passenger finds right did not build: {said}");
        Assert.True(!window.BottomPanel.Problems.Problems.Any(p => p.Path?.EndsWith("ContextsView.axaml", StringComparison.Ordinal) == true), $"The build said something of the view: {said}");
        window.Close();
    }

    /// <summary>A compiled binding under a data context Avalonia's compiler has no type for is marked as it refuses it: one a
    /// ReflectionBinding sets, and a data template's whose property takes no type from items; a template of an items
    /// control's items and a DisplayMemberBinding start from their type, a step it lacks marked, and <c>?.</c> is a step.
    /// The fix gives the element where the type is missing the x:DataType of the class that has the step; Go to
    /// Declaration takes the items' type too. Put right, the view builds.</summary>
    private void A_compiled_binding_under_a_data_context_of_no_type_is_marked_and_one_under_items_starts_from_their_type()
    {
        const string Shelf = """
            using System.Collections.Generic;

            namespace Gallery.ViewModels;

            public class ShelfViewModel
            {
                public Person Owner { get; } = new();

                public List<Person> People { get; } = [];
            }

            """;
        const string Untyped = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:Gallery.ViewModels"
                         x:DataType="vm:ShelfViewModel">
                <StackPanel>
                    <Border DataContext="{ReflectionBinding Owner}">
                        <TextBlock Text="{Binding Name}" />
                    </Border>
                    <ContentControl Content="{Binding Owner}">
                        <ContentControl.ContentTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Age}" />
                            </DataTemplate>
                        </ContentControl.ContentTemplate>
                    </ContentControl>
                    <ItemsControl ItemsSource="{Binding People}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <TextBlock Text="{Binding Nickname}" />
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                    <ListBox ItemsSource="{Binding People}" DisplayMemberBinding="{Binding Name}" />
                    <TextBlock Text="{Binding Owner?.Name}" />
                </StackPanel>
            </UserControl>

            """;
        const string NoDataType = "No x:DataType gives this compiled binding the type it binds to";
        app.Write([("ViewModels/ShelfViewModel.cs", Shelf), ("ShelfView.axaml", Untyped)]);
        MainWindow window = app.Open(app.PathOf("ShelfView.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.Markers.Count == 3, () => $"the three bindings the build refuses marked, and none it takes: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        TextSnapshot snapshot = editor.Document.Snapshot;
        Assert.Equal([(6, NoDataType), (11, NoDataType), (18, "Cannot resolve property 'Nickname' on Person")],
            document.Markers.Select(m => (snapshot.GetLineFromOffset(m.Range.Start), m.Message)).Order());

        Ui.PlaceAfter(editor, "DisplayMemberBinding=\"{Binding Na");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public string |Name", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.OpenFile(app.PathOf("ShelfView.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        document = window.EditorArea.Active!;

        Ui.PlaceAfter(editor, "<TextBlock Text=\"{Binding Na");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the reflection-bound Border's binding");
        Assert.Equal(["Add x:DataType 'Person' to Border", "Use ReflectionBinding"], window.Assist.Offered.Take(2));
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("<Border DataContext=\"{ReflectionBinding Owner}\" x:DataType=\"vm:Person\">", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.PlaceAfter(editor, "{Binding Ag");
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes offered on the data template's binding");
        Assert.Equal(["Add x:DataType 'Person' to DataTemplate", "Use ReflectionBinding"], window.Assist.Offered.Take(2));
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Replace(editor, "{Binding Nickname}", "{Binding Age}");
        Ui.WaitFor(() => document.Markers.Count == 0, () => $"every binding resolved once put right: {string.Join("; ", document.Markers.Select(m => m.Message))}");

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string said = $"{Ui.Said(window)}: {string.Join("; ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code} {p.Where} {p.Message}"))}";
        Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The view Passenger finds right did not build: {said}");
        Assert.True(!window.BottomPanel.Problems.Problems.Any(p => p.Path?.EndsWith("ShelfView.axaml", StringComparison.Ordinal) == true), $"The build said something of the view: {said}");
        window.Close();
    }

    /// <summary>After a build the XAML compiler's error (a number only it reads) is marked on its line and listed with its
    /// code. Avalonia's XAML compiler counts lines as an XML reader does, ending one at the lone '\r' in the window's tag,
    /// which the editor reads inside its line, so below it the compiler's line runs one ahead of the editor's: its error is
    /// listed, marked, said in its message and gone to from the Build window's link at the editor's line, and an agent is
    /// told that line. The line edited, the build's word on it is the build's, no longer this text's.</summary>
    private void After_a_build_the_xaml_compilers_error_below_a_lone_carriage_return_is_listed_marked_and_gone_to_at_the_editors_line()
    {
        string xaml = app.Write("MainWindow.axaml", Gallery.Window
            .Replace("d:DesignWidth=\"800\" d:DesignHeight", "d:DesignWidth=\"800\"\r d:DesignHeight", StringComparison.Ordinal)
            .Replace("<TextBlock Name=\"Title\"", "<TextBlock Name=\"Title\" Width=\"wide\"", StringComparison.Ordinal));
        MainWindow window = app.Open(xaml);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        string text = editor.Document.Snapshot.ToString();
        TextPosition width = editor.Document.Snapshot.GetPosition(text.IndexOf("Width=\"wide\"", StringComparison.Ordinal));
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Problem built = Assert.Single(window.BottomPanel.Problems.Problems, p => p.Code == "AVLN1000");
        Assert.True(built.Position == width, $"The XAML compiler's error is listed at {built.Where}, not MainWindow.axaml:{width.Line + 1}:{width.Column + 1}");
        Assert.Contains($"Line {width.Line + 1}, position {width.Column + 1}", built.Message, StringComparison.Ordinal);
        Marker marked = MarkerOn(editor, document, "Width");
        Assert.Equal(MarkerKind.Error, marked.Kind);
        Assert.StartsWith("AVLN1000: The input string 'wide' was not in a correct format.", marked.Message, StringComparison.Ordinal);

        // The compiler's message in the Build window names its own line, one on; its link goes to the editor's.
        Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Build;
        console.Flush();
        string message = console.Document.Snapshot.ToString().Split('\n').First(l => l.Contains("AVLN1000", StringComparison.Ordinal));
        Assert.True(message.Contains($"MainWindow.axaml({width.Line + 2},{width.Column + 1}", StringComparison.Ordinal), $"The Build window does not name the compiler's line: {message}");
        editor.GoTo(new TextPosition(0, 0));
        Ui.ClickLink(console, message);
        Assert.Equal(PathNames.Canonical(xaml), window.EditorArea.Active?.Path);
        Assert.True(editor.CaretPosition == width, $"The Build window's link went to {editor.CaretPosition.Line + 1}:{editor.CaretPosition.Column + 1}, not {width.Line + 1}:{width.Column + 1}");

        Passenger.App.Mcp.ProblemsInfo told = (Passenger.App.Mcp.ProblemsInfo)Ui.Await(new Passenger.App.Mcp.PassengerTools(window).Diagnostics(), "the problems an agent asks for");
        Assert.Equal((width.Line + 1, width.Column + 1), told.Build.Where(f => f.Path == "MainWindow.axaml").Select(f => (f.Line, f.Column)).Single());

        Replace(editor, "\"wide\"", "\"120\"");
        Ui.Settle();
        Assert.Empty(document.Markers);
        window.Close();
    }

    private void Passengers_own_xaml_which_compiles_is_marked_with_no_problem()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        string[] files = [.. Directory.EnumerateFiles(Path.Combine(Repository, "src", "Passenger.App"), "*.axaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
        Assert.True(files.Length >= 9, $"Passenger.App has only {files.Length} XAML files");
        foreach (string file in files)
        {
            window.OpenFile(file);
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(window.XamlService.For(document)?.IsLoaded, $"{Path.GetFileName(file)} was not read as XAML");
            Assert.True(document.Markers.Count == 0,
                $"{Path.GetFileName(file)}, which compiles, is marked: {string.Join("; ", document.Markers.Select(m => $"{document.Text.Snapshot.GetPosition(m.Range.Start).Line + 1}: {m.Message}"))}");
        }
        window.Close();
    }

    private void Go_to_declaration_from_xaml_opens_a_controls_class_a_resources_definition_and_a_bound_property()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "x:Class=\"Gallery.Main");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.axaml.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public partial class |MainWindow", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Butt");
        Ui.Run(window, AppActions.QuickDocumentation);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card describing the tag's type");
        Assert.Equal("class Avalonia.Controls.Button : ContentControl", window.Assist.HoverText);
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the class and its control theme to choose from");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        OpenDocument library = window.EditorArea.Active!;
        Assert.Equal("Avalonia.Controls.Button.cs", library.Name);
        Assert.True(library.IsReadOnly, "the library's code opened editable");
        Assert.Contains("class |Button", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "{StaticResource Acc");
        Ui.Run(window, AppActions.GoToDeclaration);
        // A key the theme's dark and light dictionaries both define: a place to choose for each.
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "Accent's dark and light definitions to choose from");
        Assert.Equal(["Accent (Dark) in Palette.axaml", "Accent (Light) in Palette.axaml"], window.Assist.Offered);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("Palette.axaml", window.EditorArea.Active?.Name);
        Assert.Contains("<SolidColorBrush x:Key=\"|Accent\" Color=\"#3574F0\" />", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "{Binding Gree");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainViewModel.cs", window.EditorArea.Active?.Name);
        Assert.Contains("public string |Greeting", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();
    }

    private void An_elements_name_renames_with_its_code_behinds_uses_and_go_to_declaration_from_the_code_lands_on_it()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        CodeEditor code = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(code, "        G");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("MainWindow.axaml", window.EditorArea.Active?.Name);
        CodeEditor xaml = window.EditorArea.CodeEditor;
        Assert.Contains("<Button Name=\"|Go\"", CaretLine(xaml), StringComparison.Ordinal);

        Ui.Run(window, AppActions.FindUsages);
        Ui.Settle();
        Ui.WaitFor(() => window.Find.Rows.Count > 0, "the usages in the Find window");
        IReadOnlyList<string> found = [.. window.Find.Rows.Where(r => r.Usage is not null).Select(r => r.Label)];
        Assert.Contains(found, f => f.Contains("Go.Content = \"Start\";", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("{Binding #Go.Content}", StringComparison.Ordinal));

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Go", "the rename box on the button's name");
        window.KeyTextInput("Start");
        Ui.Press(window, "ENTER");
        OpenDocument behind = window.EditorArea.Documents.First(d => d.Name == "MainWindow.axaml.cs");
        Ui.WaitFor(() => behind.Text.Snapshot.ToString().Contains("Start.Content = \"Start\";", StringComparison.Ordinal), "the code-behind's use renamed with it");
        string renamed = window.EditorArea.Documents.First(d => d.Name == "MainWindow.axaml").Text.Snapshot.ToString();
        Assert.Contains("<Button Name=\"Start\"", renamed, StringComparison.Ordinal);
        Assert.Contains("{Binding #Start.Content}", renamed, StringComparison.Ordinal);
        Assert.DoesNotContain("Name=\"Go\"", renamed, StringComparison.Ordinal);
        List<string> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.Equal(["Undo Rename Go to Start? It changes MainWindow.axaml.cs and MainWindow.axaml."], asked);
        Assert.Contains("Go.Content = \"Start\";", behind.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A view model's property the window binds and the window's handler of its button's Click each have that XAML
    /// among their usages: Find Usages from the C# lists it, as Find Usages from the XAML lists the C#, and Safe Delete
    /// refuses each naming it, the app still building once refused. Deleted anyway, the handler breaks the build at the
    /// line Safe Delete named.</summary>
    private void A_bound_property_and_a_handler_have_their_xaml_among_their_usages_and_safe_delete_names_it_before_breaking_the_build()
    {
        app.Rewrite("MainWindow.axaml", Gallery.Handled(Gallery.Window));
        app.Rewrite("MainWindow.axaml.cs", Gallery.HandlingCodeBehind);
        MainWindow window = app.Open(app.PathOf("ViewModels/MainViewModel.cs"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "string Gree");
        Assert.Contains("MainWindow.axaml:11 <TextBlock Name=\"Title\" Text=\"{Binding Greeting}\" />", FoundUsages(window));
        Ui.Run(window, AppActions.SafeDelete);
        AssertListed(window, "The property 'Greeting' has 1 usage", "MainWindow.axaml:11", "<TextBlock Name=\"Title\" Text=\"{Binding Greeting}\" />");
        Assert.Contains("public string Greeting", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Equal("Safe Delete left the property 'Greeting' where it is", window.Status);

        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{Binding Gree");
        Assert.Contains("MainViewModel.cs:5 public string Greeting { get; set; } = \"Hello\";", FoundUsages(window));

        window.OpenFile(app.PathOf("MainWindow.axaml.cs"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "void On");
        const string Clicked = "<Button Name=\"Go\" Content=\"Go\" Click=\"OnGo\" Background=\"{StaticResource Accent}\" />";
        Assert.Contains($"MainWindow.axaml:12 {Clicked}", FoundUsages(window));
        Ui.Run(window, AppActions.SafeDelete);
        AssertListed(window, "The method 'OnGo' has 1 usage", "MainWindow.axaml:12", Clicked);
        Built(window);

        Ui.Run(window, AppActions.SafeDelete);
        Ui.ClickButton(Listed(window), "Delete Anyway");
        Ui.WaitFor(() => !editor.Document.Snapshot.ToString().Contains("OnGo", StringComparison.Ordinal), "the handler to be deleted anyway");
        window.Perform(AppActions.Build);
        Ui.WaitFor(() => !window.IsBuilding, "the build without the handler", TimeSpan.FromMinutes(2));
        Assert.Contains(window.BottomPanel.Problems.Problems, p => p.Code == "AVLN3000" && Path.GetFileName(p.Path) == "MainWindow.axaml" && p.Position.Line == 11);
        window.Close();

        static void Built(MainWindow window)
        {
            window.Perform(AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
            Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The app did not build once Safe Delete was refused: {Ui.Said(window)}");
        }
    }

    /// <summary>Find Usages of a view model's property no XAML file names reads no project's XAML, which would read every C#
    /// file of the project for its types; one the window binds has its project's XAML read once, the binding among its
    /// usages.</summary>
    private void Find_usages_of_a_property_no_xaml_names_reads_no_projects_xaml_and_of_one_bound_reads_it_once()
    {
        MainWindow window = app.Open(app.PathOf("ViewModels/MainViewModel.cs"));
        Assert.True(window.XamlService.ProjectsRead == 0, $"{window.XamlService.ProjectsRead} projects' XAML was read before any Find Usages, so what one reads cannot be told");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "int Cou");
        IReadOnlyList<string> counted = FoundUsages(window);
        Assert.True(window.XamlService.ProjectsRead == 0, "Find Usages of Count, which no XAML file names, read the project's XAML and all its C# to look for uses");
        Assert.DoesNotContain(counted, u => u.Contains(".axaml:", StringComparison.Ordinal));

        Ui.PlaceAfter(window.EditorArea.CodeEditor, "string Gree");
        Assert.Contains("MainWindow.axaml:11 <TextBlock Name=\"Title\" Text=\"{Binding Greeting}\" />", FoundUsages(window));
        Assert.Equal(1, window.XamlService.ProjectsRead);
        window.Close();
    }

    /// <summary>A view model's property used only in a <c>&lt;Binding&gt;</c> element, a <c>&lt;Binding.Path&gt;</c>'s
    /// text, a <c>?.</c> path or a step after an indexer has that XAML among its usages: Find Usages from the C# lists it,
    /// Safe Delete refuses naming it, and Go to Declaration from each lands on the property.</summary>
    private void A_property_bound_by_an_element_its_path_text_a_null_conditional_path_or_after_an_indexer_has_that_xaml_among_its_usages()
    {
        const string Forms = """
            using System.Collections.Generic;

            namespace Gallery.ViewModels;

            public class FormsViewModel
            {
                public string InElement { get; set; } = "";

                public string InPathText { get; set; } = "";

                public Person? Chosen { get; set; }

                public List<FormsViewModel> Children { get; } = [];

                public string AfterIndex { get; set; } = "";
            }

            """;
        const string View = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:Gallery.ViewModels"
                         x:DataType="vm:FormsViewModel">
                <StackPanel>
                    <TextBlock>
                        <TextBlock.Text>
                            <MultiBinding StringFormat="{}{0}">
                                <Binding Path="InElement" />
                            </MultiBinding>
                        </TextBlock.Text>
                    </TextBlock>
                    <TextBlock>
                        <TextBlock.Text>
                            <Binding>
                                <Binding.Path>InPathText</Binding.Path>
                            </Binding>
                        </TextBlock.Text>
                    </TextBlock>
                    <TextBlock Text="{Binding Chosen?.Name}" />
                    <TextBlock Text="{Binding Children[0].AfterIndex}" />
                </StackPanel>
            </UserControl>

            """;
        app.Write([("ViewModels/FormsViewModel.cs", Forms), ("FormsView.axaml", View)]);
        (string Declared, string Property, int Line, string Code, string Bound)[] uses =
        [
            ("string InEl", "InElement", 9, "<Binding Path=\"InElement\" />", "<Binding Path=\"InEl"),
            ("string InPa", "InPathText", 16, "<Binding.Path>InPathText</Binding.Path>", "<Binding.Path>InPa"),
            ("Person? Cho", "Chosen", 20, "<TextBlock Text=\"{Binding Chosen?.Name}\" />", "{Binding Cho"),
            ("string AfterI", "AfterIndex", 21, "<TextBlock Text=\"{Binding Children[0].AfterIndex}\" />", "Children[0].AfterI"),
        ];
        MainWindow window = app.Open(app.PathOf("ViewModels/FormsViewModel.cs"));
        foreach ((string declared, string property, int line, string code, _) in uses)
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, declared);
            IReadOnlyList<string> found = FoundUsages(window);
            Assert.True(found.Contains($"FormsView.axaml:{line} {code}"), $"Find Usages of {property} left out the XAML that binds it, line {line}: {string.Join(" | ", found)}");
            Ui.Run(window, AppActions.SafeDelete);
            AssertListed(window, $"The property '{property}' has 1 usage", $"FormsView.axaml:{line}", code);
        }

        foreach ((string declared, string property, _, _, string bound) in uses)
        {
            window.OpenFile(app.PathOf("FormsView.axaml"));
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, bound);
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.Settle();
            string landed = $"public {declared[..declared.IndexOf(' ')]} |{property}";
            Assert.True(window.EditorArea.Active?.Name == "FormsViewModel.cs" && CaretLine(window.EditorArea.CodeEditor).Contains(landed, StringComparison.Ordinal),
                $"Go to Declaration from the XAML binding {property} did not land on it: {window.EditorArea.Active?.Name}: {CaretLine(window.EditorArea.CodeEditor)}");
        }
        window.Close();
    }

    /// <summary>The gallery's view model with a command and a static member beside the property its window binds.</summary>
    private const string BoundViewModel = """
        using System;
        using System.Windows.Input;

        namespace Gallery.ViewModels;

        public class MainViewModel
        {
            public string Greeting { get; set; } = "Hello";

            public Person Owner { get; } = new();

            public int Count { get; set; }

            public ICommand Save { get; } = new Relay();

            public static string Motto => "Tea";
        }

        public class Person
        {
            public string Name { get; set; } = "Ada";

            public int Age { get; set; }
        }

        public sealed class Relay : ICommand
        {
            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter)
            {
            }
        }

        """;

    /// <summary>The gallery's window binding the view model's property three ways under its x:DataType, its command, and its
    /// static member through x:Static, its button's Click handled by the code-behind.</summary>
    private static readonly string BoundWindow = Gallery.Handled(Gallery.Window).Replace("    <TextBlock Text=\"{Binding #Go.Content}\" />\n",
        "    <TextBlock Text=\"{Binding #Go.Content}\" />\n        <TextBox Text=\"{Binding Path=Greeting, Mode=TwoWay}\" />\n"
        + "        <TextBlock Text=\"{CompiledBinding Greeting}\" />\n        <TextBlock Text=\"{x:Static vm:MainViewModel.Motto}\" />\n"
        + "        <Button Content=\"Save\" Command=\"{Binding Save}\" />\n", StringComparison.Ordinal);

    /// <summary>A view model's property, command and static member and the window's handler, each renamed from its C# in the
    /// window, take along the XAML that uses them (bindings by path under the window's x:DataType, a compiled binding,
    /// x:Static, a Click), the window's XAML not open; one Undo puts the property's rename back in both files, and the app
    /// builds once all are renamed, its compiled bindings and handler resolving each new name.</summary>
    private void Renaming_a_bound_property_a_command_a_static_member_and_a_handler_in_csharp_renames_the_xaml_using_them_and_the_app_builds()
    {
        app.Rewrite("ViewModels/MainViewModel.cs", BoundViewModel);
        app.Rewrite("MainWindow.axaml", BoundWindow);
        app.Rewrite("MainWindow.axaml.cs", Gallery.HandlingCodeBehind);
        string view = app.PathOf("MainWindow.axaml");
        MainWindow window = app.Open(app.PathOf("ViewModels/MainViewModel.cs"));
        List<string> asked = [];
        window.Confirm = (_, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        OpenDocument model = window.EditorArea.Active!;

        RenameInCode(window, "string Gree", "Salutation");
        string salutation = BoundWindow.Replace("Binding Greeting}", "Binding Salutation}", StringComparison.Ordinal)
            .Replace("Path=Greeting", "Path=Salutation", StringComparison.Ordinal).Replace("CompiledBinding Greeting", "CompiledBinding Salutation", StringComparison.Ordinal);
        Assert.True(File.ReadAllText(view) == salutation, $"The window's bindings did not follow the property's rename ({window.Status}):\n{File.ReadAllText(view)}");
        Assert.Equal(BoundViewModel.Replace("string Greeting", "string Salutation", StringComparison.Ordinal), model.Text.Snapshot.ToString());
        Assert.Equal("Renamed in 2 files", window.Status);

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.True(asked.Count == 1 && asked[0].StartsWith("Undo Rename Greeting to Salutation? It changes ", StringComparison.Ordinal), $"The person's Undo asked: {string.Join(" | ", asked)}");
        Assert.True(File.ReadAllText(view) == BoundWindow, $"One Undo did not put the window's bindings back:\n{File.ReadAllText(view)}");
        Assert.Equal(BoundViewModel, model.Text.Snapshot.ToString());

        RenameInCode(window, "string Gree", "Salutation");
        RenameInCode(window, "ICommand Sa", "Store");
        RenameInCode(window, "string Mot", "Slogan");
        window.OpenFile(app.PathOf("MainWindow.axaml.cs"));
        Ui.Settle();
        RenameInCode(window, "void OnG", "OnStart");
        string all = salutation.Replace("Command=\"{Binding Save}\"", "Command=\"{Binding Store}\"", StringComparison.Ordinal)
            .Replace("MainViewModel.Motto", "MainViewModel.Slogan", StringComparison.Ordinal).Replace("Click=\"OnGo\"", "Click=\"OnStart\"", StringComparison.Ordinal);
        Assert.True(File.ReadAllText(view) == all, $"The window's XAML did not follow the command's, the static member's and the handler's renames ({window.Status}):\n{File.ReadAllText(view)}");

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string said = $"{Ui.Said(window)}: {string.Join("; ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code} {p.Where} {p.Message}"))}";
        Assert.True(window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true, $"The app did not build once its XAML followed the renames: {said}");
        window.Close();
    }

    /// <summary>Rename at the end of a piece of the active file, the new name typed into its box and Enter pressed.</summary>
    internal static void RenameInCode(MainWindow window, string after, string name)
    {
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming is not null, "the rename box to open");
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
        Ui.Settle();
    }

    /// <summary>Safe Delete's question names the XAML that would no longer compile, which the test then declines.</summary>
    private static void AssertListed(MainWindow window, string said, string where, string code)
    {
        ConfirmDialog asked = Listed(window);
        string[] shown = [.. asked.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty)];
        Assert.True(shown.Contains($"{said} that would no longer compile once it is deleted:") && shown.Contains(where) && shown.Contains(code),
            $"Safe Delete did not name the XAML that would no longer compile, {where}: {string.Join(" | ", shown)}");
        Ui.ClickButton(asked, "Cancel");
    }

    private static ConfirmDialog Listed(MainWindow window)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "Safe Delete to list the usages");
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single());
    }

    /// <summary>The usages Find Usages lists from the caret, each as its file's name, its line and its code.</summary>
    internal static IReadOnlyList<string> FoundUsages(MainWindow window)
    {
        int searches = window.Find.Searches.Count;
        Ui.Run(window, AppActions.FindUsages);
        Ui.WaitFor(() => window.Find.Searches.Count > searches, "the usages in the Find window");
        return [.. window.Find.Rows.Where(r => r.Usage is not null).Select(r => $"{Path.GetFileName(r.Usage!.Path)}:{r.Usage.Line + 1} {r.Label}")];
    }

    /// <summary>A line written at the top of the open code-behind while an element's rename in its XAML is out (from another
    /// split, or an agent) goes in above the code, and the rename still lands on the code-behind's use of the element, as
    /// on the XAML's, where the server was asked about the place the use had been and the rename landed elsewhere.</summary>
    private void A_line_written_in_the_code_behind_while_an_elements_rename_is_out_still_renames_its_use_there()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        OpenDocument behind = window.EditorArea.Active!;
        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        OpenDocument view = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Button Name=\"G");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Go", "the rename box on the button's name");
        window.KeyTextInput("Start");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        behind.Text.Edit([new TextEdit(0, 0, "// written meanwhile\n")], behind.Selections, behind.Selections);
        Ui.WaitFor(() => view.Text.Snapshot.ToString().Contains("<Button Name=\"Start\"", StringComparison.Ordinal), $"the button renamed: {window.Status}");
        Ui.Settle();

        Assert.Equal("// written meanwhile\n" + Gallery.CodeBehind.Replace("Go.Content", "Start.Content", StringComparison.Ordinal), behind.Text.Snapshot.ToString());
        Assert.Contains("{Binding #Start.Content}", view.Text.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>An element renamed to a C# keyword, to another element's name, or to the name of its Click's handler in the
    /// code-behind is refused, saying why, and neither file changes: the generated field and the code-behind's use of it
    /// would not be C#, the window would throw as it loads, its two names registered once, or the generated field would
    /// clash with the handler.</summary>
    private void An_elements_name_is_not_renamed_to_a_csharp_keyword_another_elements_or_its_handlers_and_its_files_are_left_as_they_were()
    {
        string handled = Gallery.Handled(Gallery.Window);
        app.Rewrite("MainWindow.axaml", handled);
        app.Rewrite("MainWindow.axaml.cs", Gallery.HandlingCodeBehind);
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        OpenDocument behind = window.EditorArea.Active!;
        window.OpenFile(app.PathOf("MainWindow.axaml"));
        Ui.Settle();
        OpenDocument view = window.EditorArea.Active!;
        foreach ((string name, string said, string broken) in ((string, string, string)[])[
            ("class", "'class' is a C# keyword, not a name for the code-behind's field", "the code-behind writes class.Content, which is not C#"),
            ("Title", "'Title' names another element already, on line 11", "two of the window's names are Title"),
            ("OnGo", "'OnGo' names a method of MainWindow already", "the generated field clashes with the handler")])
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Button Name=\"G");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Go", "the rename box on the button's name");
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.Equal(said, window.Status);
            Assert.True(view.Text.Snapshot.ToString() == handled && behind.Text.Snapshot.ToString() == Gallery.HandlingCodeBehind,
                $"The rename to {name} changed the files, so {broken}:\n{view.Text.Snapshot}\n{behind.Text.Snapshot}");
        }
        window.Close();
    }

    /// <summary>Rename on a named element's field in the code-behind renames the element: its name in the XAML, which is not
    /// open, the XAML's use of it and the code-behind's, as one command undone whole.</summary>
    private void An_elements_field_renamed_from_the_code_behind_renames_the_element_in_its_xaml_and_every_use()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        OpenDocument behind = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "        G");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Go", "the rename box on the field's name");
        window.KeyTextInput("Start");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => window.Status?.Contains("ename", StringComparison.Ordinal) == true, () => $"the rename's word, the status saying {window.Status}");
        Ui.Settle();
        string view = File.ReadAllText(app.PathOf("MainWindow.axaml"));
        Assert.True(window.Status == "Renamed in 2 files" && behind.Text.Snapshot.ToString() == Gallery.CodeBehind.Replace("Go.Content", "Start.Content", StringComparison.Ordinal)
            && view == Gallery.Window.Replace("Name=\"Go\"", "Name=\"Start\"", StringComparison.Ordinal).Replace("#Go.", "#Start.", StringComparison.Ordinal),
            $"The field's rename from the code-behind said {window.Status}, leaving the code-behind\n{behind.Text.Snapshot}\nand the XAML\n{view}");

        List<string> asked = [];
        window.Confirm = (title, text, _) =>
        {
            asked.Add(text);
            return Task.FromResult(true);
        };
        Ui.Run(window, EditorActions.Undo);
        Ui.Settle();
        Assert.True(behind.Text.Snapshot.ToString() == Gallery.CodeBehind && File.ReadAllText(app.PathOf("MainWindow.axaml")) == Gallery.Window,
            $"Undo of the rename ({string.Join(" | ", asked)}) did not put both files back:\n{behind.Text.Snapshot}\n{File.ReadAllText(app.PathOf("MainWindow.axaml"))}");
        window.Close();
    }

    /// <summary>Rename on a named element's field in the code-behind a moment after the element's rename was written to its
    /// XAML on disk, as an agent's rename writes it, the language server not yet told of the XAML's change and so knowing
    /// no field of the new name: the rename waits for the server to read the XAML, saying so, however long the steps
    /// between took (a loaded machine's), then renames the element in it and the field's use.</summary>
    private void An_elements_field_renamed_from_the_code_behind_a_moment_after_its_xaml_was_written_waits_for_the_server_to_read_it()
    {
        MainWindow window = OpenWithTheXamlRead();
        OpenDocument behind = window.EditorArea.Active!;
        Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
        TaskCompletionSource told = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task held = server.HoldNext(Passenger.Lsp.LspMethods.DidChangeWatchedFiles,
            p => System.Text.Json.JsonSerializer.Serialize(p).Contains("MainWindow.axaml\"", StringComparison.Ordinal), told.Task);
        try
        {
            string started = Gallery.Window.Replace("Name=\"Go\"", "Name=\"Start\"", StringComparison.Ordinal).Replace("#Go.", "#Start.", StringComparison.Ordinal);
            TestFolders.Write(app.Folder, "MainWindow.axaml", started);
            Replace(window.EditorArea.CodeEditor, "Go.Content", "Start.Content");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "        Sta");
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == "Start", "the rename box on the field's name");
            window.KeyTextInput("Begin");
            Ui.Press(window, "ENTER", settle: false);
            // The rename waits for the server for up to its patience's limit, its first answer included.
            TimeSpan within = Passenger.App.CSharp.Patience.Limit;
            Assert.True(Ui.Pumped(() => window.Status?.StartsWith("Waiting for the language server to read MainWindow.axaml", StringComparison.Ordinal) == true, within),
                $"Within {within.Scaled().TotalSeconds:0} s the rename did not say it waits for the language server to read the XAML written a moment before, "
                + $"which knows no field of the new name yet ({(held.IsCompleted ? "the XAML's change was held unsent to the server" : "the watcher had not told the server of the XAML's change")}): "
                + $"the status says {window.Status}");
        }
        finally
        {
            told.TrySetResult();
        }
        Ui.WaitFor(() => window.Status?.Contains("ename", StringComparison.Ordinal) == true, () => $"the rename's word, the status saying {window.Status}");
        Ui.Settle();
        string view = File.ReadAllText(app.PathOf("MainWindow.axaml"));
        Assert.True(window.Status == "Renamed in 2 files" && behind.Text.Snapshot.ToString() == Gallery.CodeBehind.Replace("Go.Content", "Begin.Content", StringComparison.Ordinal)
            && view == Gallery.Window.Replace("Name=\"Go\"", "Name=\"Begin\"", StringComparison.Ordinal).Replace("#Go.", "#Begin.", StringComparison.Ordinal),
            $"The field's rename from the code-behind said {window.Status}, leaving the code-behind\n{behind.Text.Snapshot}\nand the XAML\n{view}");
        window.Close();
    }

    /// <summary>A XAML file saved, which the language server reads from disk, is kept as changing from its save, before the
    /// folder's watcher can tell of it: a command asked past the moment after the write still waits for the server to read
    /// it, however late the watcher's word comes on a loaded machine.</summary>
    private void A_xaml_file_saved_is_kept_for_the_language_server_to_read_before_the_folders_watcher_tells_of_it()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        Ui.Settle();
        Passenger.Lsp.WatchedFiles watched = window.CSharpService!.Session!.Server.WatchedFiles;
        Replace(window.EditorArea.CodeEditor, "Content=\"Go\"", "Content=\"Begin\"");
        long before = watched.Changing().AsOf;
        // No turn of the window's loop between the save and the look: the watcher's word comes by one.
        Assert.True(window.EditorArea.Save(view), "The XAML file was not saved");
        (IReadOnlyList<string> changing, long asOf) = watched.Changing();
        Assert.True(asOf > before && changing.Any(p => PathNames.Same(p, view.Path)),
            $"The save was not kept for the server to read until the folder's watcher told of it, so a command asked past the moment after the write takes nothing for behind: {string.Join(", ", changing)}");
        window.Close();
    }

    /// <summary>A window on the code-behind with a language server of its own, started on the project as written, that holds
    /// MainWindow.axaml as on disk and was told of no change of it: a server taken up warm may have been told of the XAML put
    /// back for this test, and reads it only when next asked, so after the test's own write, knowing the new field though the
    /// watcher's word of the write is held.</summary>
    private MainWindow OpenWithTheXamlRead()
    {
        app.Drop();
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml.cs"));
        Ui.Settle();
        Passenger.Lsp.RoslynSession session = window.CSharpService!.Session!;
        string xaml = PathNames.Canonical(app.PathOf("MainWindow.axaml"));
        IReadOnlyList<Passenger.Roslyn.Extension.TextHeld> held = Ui.Await(session.HeldTextsAsync([xaml]), "the language server's text of MainWindow.axaml");
        bool asOnDisk = held is [{ Hash: { } hash }] && Passenger.Lsp.HeldTexts.OnDisk(xaml, hash) == true;
        long? told = session.Server.WatchedFiles.ToldAt(xaml);
        Assert.True(asOnDisk && told is null,
            $"The language server started for the test {(asOnDisk ? "holds MainWindow.axaml as on disk" : "holds MainWindow.axaml otherwise than the disk, or no document of it")} "
            + $"and {(told is { } at ? $"was told of a change of it {Stopwatch.GetElapsedTime(at).TotalSeconds:0.00} s ago" : "was told of no change of it")}, so it may read the XAML "
            + "the test writes with no word of the write, leaving the rename nothing to wait for");
        return window;
    }

    private void A_key_one_theme_variant_lacks_is_warned_of_where_defined_and_used_and_the_fix_adds_it_to_that_variant()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument view = window.EditorArea.Active!;
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Button Name=\"Go\" Content=\"Go\"");
        window.KeyTextInput(" Foreground=\"{DynamicResource Danger}\"");
        window.OpenFile(app.PathOf("Styles/Palette.axaml"));
        Ui.Settle();
        CodeEditor palette = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.PlaceAfter(palette, "<SolidColorBrush x:Key=\"Muted\" Color=\"#868A91\" />");
        window.KeyTextInput("\n            <SolidColorBrush x:Key=\"Danger\" Color=\"#E55765\" />");
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning on the key only Dark defines");
        Assert.Equal("'Danger' is defined for Dark but not for Light", MarkerOn(palette, document, "Danger").Message);
        Ui.WaitFor(() => view.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning on the window's use of it, from the palette as typed");
        Marker used = Assert.Single(view.Markers);
        Assert.Equal("'Danger' is not defined for Light", used.Message);

        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fix offered on the key's line");
        Assert.Equal(["Add 'Danger' to Light"], window.Assist.Offered);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("<SolidColorBrush x:Key=\"Muted\" Color=\"#6C707E\" />\n            <SolidColorBrush x:Key=\"Danger\" Color=\"#E55765\" />\n        </ResourceDictionary>",
            palette.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.WaitFor(() => document.Markers.Count == 0 && view.Markers.Count == 0, "both warnings gone once Light has the key too");
        window.Close();
    }

    private void An_avares_path_completes_from_the_projects_assets_is_warned_of_when_missing_and_goes_to_its_file()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "<StackPanel Margin=\"12\" Spacing=\"8\">");
        window.KeyTextInput("\n        <Image Source=");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("avares://Gallery/Assets/logo.png"), "the project's asset as an avares:// path");
        Assert.Contains("avares://Gallery/Styles/Palette.axaml", window.Assist.CompletionLabels);
        window.KeyTextInput("logo");
        Ui.Settle();
        Assert.Equal("avares://Gallery/Assets/logo.png", window.Assist.CompletionLabels.FirstOrDefault());
        Ui.Press(window, "ENTER");
        Assert.EndsWith("<Image Source=\"avares://Gallery/Assets/logo.png|\"", CaretLine(editor), StringComparison.Ordinal);
        Ui.Press(window, "END");
        window.KeyTextInput(" />");
        Ui.Settle();
        Assert.Empty(document.Markers);

        Replace(editor, "logo.png", "logo2.png");
        Ui.WaitFor(() => document.Markers.Count == 1, "the warning on the asset that is not there");
        Assert.Equal("'avares://Gallery/Assets/logo2.png' is not found", document.Markers[0].Message);
        Assert.Equal(MarkerKind.Warning, document.Markers[0].Kind);

        window.OpenFile(app.PathOf("App.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Source=\"avares://Gallery/Sty");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Palette.axaml", window.EditorArea.Active?.Name);
        window.Close();
    }

    private void Colours_show_as_gutter_swatches_and_one_chosen_in_the_picker_is_written_back_as_it_was_written()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument view = window.EditorArea.Active!;
        int button = editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("<Button Name=\"Go\"", StringComparison.Ordinal));
        // The button's background names the palette's Accent: its swatch is the dark theme's, which the window shows.
        Ui.WaitFor(() => view.ColourMarks.Any(m => m.Line == button), "the swatch of the brush the button's background names");
        Assert.Equal(Color.Parse("#3D7BF5"), view.ColourMarks.Single(m => m.Line == button).Colour);
        Point swatch = editor.TranslatePoint(editor.GutterColumnPoint(editor.Gutter!.Marks, button), window)!.Value;
        Assert.Equal(Color.Parse("#3D7BF5"), Ui.Pixel(window, swatch));

        window.MouseDown(swatch, MouseButton.Left);
        window.MouseUp(swatch, MouseButton.Left);
        Ui.Settle();
        Assert.Equal("#3D7BF5", window.PickingColour);
        window.KeyTextInput("#E2A33F");
        Ui.Settle();
        Assert.Equal("#E2A33F", window.PickingColour);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Null(window.PickingColour);
        string palette = File.ReadAllText(app.PathOf("Styles/Palette.axaml"));
        Assert.Contains("<SolidColorBrush x:Key=\"Accent\" Color=\"#E2A33F\" />", palette, StringComparison.Ordinal);
        Assert.Contains("<SolidColorBrush x:Key=\"Accent\" Color=\"#3574F0\" />", palette, StringComparison.Ordinal);
        Ui.WaitFor(() => view.ColourMarks.SingleOrDefault(m => m.Line == button).Colour == Color.Parse("#E2A33F"), "the swatch following the palette");

        window.OpenFile(app.PathOf("MainWindow.axaml.cs"));
        Ui.Settle();
        CodeEditor code = window.EditorArea.CodeEditor;
        OpenDocument behind = window.EditorArea.Active!;
        Ui.PlaceAfter(code, "Go.Content = \"Start\";");
        window.KeyTextInput("\n        Go.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x33, 0x66, 0x99));");
        int line = code.CaretPosition.Line;
        Ui.WaitFor(() => behind.ColourMarks.Any(m => m.Line == line), "the swatch of the colour FromRgb makes");
        Assert.Equal(Color.Parse("#336699"), behind.ColourMarks.Single(m => m.Line == line).Colour);
        Ui.Click(code, code.GutterColumnPoint(code.Gutter!.Marks, line));
        window.KeyTextInput("#AA0000");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Contains("new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(170, 0, 0));", code.Document.Snapshot.GetLineText(line), StringComparison.Ordinal);
        window.Close();
    }

    private void Go_to_declaration_reaches_the_themes_own_xaml_for_a_control_a_template_part_and_a_theme_resource()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        // Fluent's sources as its Source Link gives them, stood in: FluentControls.xaml merges Button.xaml in.
        ThemeSources sources = new(TestFolders.Create("xaml-theme-"));
        window.XamlService.LibraryFiles = sources;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<Butt");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the class and its control theme to choose from");
        Assert.Equal(["Avalonia.Controls.Button", "Button's control theme"], window.Assist.Offered);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        OpenDocument theme = window.EditorArea.Active!;
        Assert.Equal("Button.xaml", theme.Name);
        Assert.True(theme.IsReadOnly, "the theme's XAML opened editable");
        Assert.Contains("<|ControlTheme x:Key=\"{x:Type Button}\" TargetType=\"Button\">", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        Assert.Equal([("Avalonia.Themes.Fluent", "/FluentTheme.xaml"), ("Avalonia.Themes.Fluent", "/Controls/FluentControls.xaml"), ("Avalonia.Themes.Fluent", "/Controls/Button.xaml")],
            sources.Asked);

        window.OpenFile(app.PathOf("App.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "<FluentTheme />");
        window.KeyTextInput("\n        <Style Selector=\"Button /template/ ContentPresenter#PART_ContentPresenter\">\n            <Setter Property=\"Background\" Value=\"{DynamicResource ButtonBackground}\" />\n        </Style>");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "#PART_Con");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("Button.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("<ContentPresenter x:Name=\"|PART_ContentPresenter\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);

        window.OpenFile(app.PathOf("App.axaml"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "{DynamicResource ButtonBack");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.Settle();
        Assert.Equal("FluentControlResources.xaml", window.EditorArea.Active?.Name);
        Assert.Contains("x:Key=\"|ButtonBackground\"", CaretLine(window.EditorArea.CodeEditor), StringComparison.Ordinal);
        window.Close();
    }

    private void A_themes_include_that_climbs_out_of_its_sources_folder_is_refused_and_nothing_is_written_outside_it()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        Passenger.App.Xaml.XamlDocument xaml = window.XamlService.For(window.EditorArea.Active!)!;
        Ui.WaitFor(() => xaml.IsLoaded, "the project's XAML read");
        // Fluent's root and control list as Source Link gave them before, the list merging in a file five folders up.
        Passenger.Xaml.Metadata.AssemblyTypes fluent = Ui.Await(xaml.Loaded, "the project's XAML")!.Types.Assemblies.Single(a => a.Name == "Avalonia.Themes.Fluent");
        string entry = Path.Combine(Passenger.App.Xaml.LibrarySources.Folder, "SourceLink", fluent.Name,
            Passenger.App.Xaml.LibrarySources.Stamp(Passenger.App.Xaml.LibrarySources.Implementation(fluent.Path)));
        string escaped = Path.Combine(Passenger.App.State.StateStore.DefaultRoot, "escaped");
        try
        {
            Directory.CreateDirectory(Path.Combine(entry, "Controls"));
            File.WriteAllText(Path.Combine(entry, "FluentTheme.xaml"),
                "<Styles xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"Avalonia.Themes.Fluent.FluentTheme\">\n"
                + "  <Styles.Resources>\n    <ResourceDictionary>\n      <ResourceDictionary.MergedDictionaries>\n"
                + "        <MergeResourceInclude Source=\"/Controls/FluentControls.xaml\" />\n"
                + "      </ResourceDictionary.MergedDictionaries>\n    </ResourceDictionary>\n  </Styles.Resources>\n</Styles>\n");
            File.WriteAllText(Path.Combine(entry, "Controls", "FluentControls.xaml"),
                "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\">\n  <ResourceDictionary.MergedDictionaries>\n"
                + "    <MergeResourceInclude Source=\"../../../../../escaped/Button.xaml\" />\n  </ResourceDictionary.MergedDictionaries>\n</ResourceDictionary>\n");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "<Butt");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.Assist.Offered.Count == 2, "the class and its control theme to choose from");
            Ui.Press(window, "DOWN");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Status?.Contains("leads outside its sources' folder", StringComparison.Ordinal) == true, "the include that climbs out refused, saying why");
            Assert.Equal("MainWindow.axaml", window.EditorArea.Active?.Name);
            Assert.False(Directory.Exists(escaped), $"Source Link wrote outside its folder: {escaped}");
            window.Close();
        }
        finally
        {
            // The run's other tests ask Source Link for Fluent's files afresh.
            string versions = Path.Combine(Passenger.App.Xaml.LibrarySources.Folder, "SourceLink", fluent.Name);
            if (Directory.Exists(versions))
            {
                Directory.Delete(versions, recursive: true);
            }
        }
    }

    /// <summary>A theme library's files as its Source Link would give them, laid out as Fluent 12.2's are, written into a
    /// folder of the test's, each as <paramref name="written"/> rewrites it where given; what was asked for, in order. The
    /// theme's root merges in the resources its controls share (by theme variant, as Fluent keeps a control's colours) and
    /// the control list, which merges in each control's file, and is compiled into the root rather than on its own.</summary>
    private sealed class ThemeSources(string folder, Func<string, string>? written = null) : Passenger.App.Xaml.ILibraryFiles
    {
        private const string Namespaces = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        public List<(string Assembly, string Path)> Asked { get; } = [];

        public Task<string?> FileAsync(string assembly, string assemblyName, string path, IProgress<string>? why, CancellationToken cancel)
        {
            Asked.Add((assemblyName, path));
            string? text = path switch
            {
                "/FluentTheme.xaml" => $"<Styles x:Class=\"Avalonia.Themes.Fluent.FluentTheme\" {Namespaces}>\n  <Styles.Resources>\n    <ResourceDictionary>\n"
                    + "      <ResourceDictionary.MergedDictionaries>\n        <ResourceInclude Source=\"/Accents/BaseColorsPalette.xaml\" />\n"
                    + "        <MergeResourceInclude Source=\"/Accents/BaseResources.xaml\" />\n        <MergeResourceInclude Source=\"/Accents/FluentControlResources.xaml\" />\n"
                    + "        <MergeResourceInclude Source=\"/Controls/FluentControls.xaml\" />\n"
                    + "      </ResourceDictionary.MergedDictionaries>\n    </ResourceDictionary>\n  </Styles.Resources>\n</Styles>\n",
                "/Accents/BaseResources.xaml" => $"<ResourceDictionary {Namespaces} xmlns:sys=\"using:System\">\n"
                    + "  <sys:Double x:Key=\"ControlContentThemeFontSize\">14</sys:Double>\n  <ResourceDictionary.ThemeDictionaries>\n    <ResourceDictionary x:Key=\"Default\">\n"
                    + "      <SolidColorBrush x:Key=\"SystemControlForegroundBaseHighBrush\" Color=\"{StaticResource SystemBaseHighColor}\" />\n    </ResourceDictionary>\n"
                    + "    <ResourceDictionary x:Key=\"Dark\">\n      <SolidColorBrush x:Key=\"SystemControlForegroundBaseHighBrush\" Color=\"{StaticResource SystemBaseHighColor}\" />\n"
                    + "    </ResourceDictionary>\n  </ResourceDictionary.ThemeDictionaries>\n</ResourceDictionary>\n",
                "/Accents/FluentControlResources.xaml" => $"<ResourceDictionary {Namespaces}>\n  <ResourceDictionary.ThemeDictionaries>\n    <ResourceDictionary x:Key=\"Default\">\n"
                    + "      <StaticResource x:Key=\"ButtonBackground\" ResourceKey=\"ControlFillColorDefaultBrush\" />\n"
                    + "      <StaticResource x:Key=\"TextControlBackground\" ResourceKey=\"SystemControlBackgroundAltMediumLowBrush\" />\n    </ResourceDictionary>\n"
                    + "    <ResourceDictionary x:Key=\"Dark\">\n      <StaticResource x:Key=\"ButtonBackground\" ResourceKey=\"ControlFillColorDefaultBrush\" />\n"
                    + "      <StaticResource x:Key=\"TextControlBackground\" ResourceKey=\"SystemControlBackgroundAltMediumLowBrush\" />\n    </ResourceDictionary>\n"
                    + "  </ResourceDictionary.ThemeDictionaries>\n</ResourceDictionary>\n",
                "/Controls/FluentControls.xaml" => $"<ResourceDictionary {Namespaces}>\n  <ResourceDictionary.MergedDictionaries>\n"
                    + "    <MergeResourceInclude Source=\"avares://Avalonia.Themes.Fluent/Controls/Button.xaml\" />\n"
                    + "  </ResourceDictionary.MergedDictionaries>\n</ResourceDictionary>\n",
                "/Controls/Button.xaml" => $"<ResourceDictionary {Namespaces}>\n"
                    + "  <ControlTheme x:Key=\"{x:Type Button}\" TargetType=\"Button\">\n    <Setter Property=\"Template\">\n      <ControlTemplate>\n"
                    + "        <ContentPresenter x:Name=\"PART_ContentPresenter\" Content=\"{TemplateBinding Content}\" />\n      </ControlTemplate>\n    </Setter>\n"
                    + "  </ControlTheme>\n</ResourceDictionary>\n",
                _ => null,
            };
            if (text is null)
            {
                why?.Report($"{path} is not in the stand-in");
                return Task.FromResult<string?>(null);
            }
            // Kept as Source Link's files are, under a MetadataAsSource folder, which the editor shows read-only.
            string local = Path.Combine(folder, "MetadataAsSource", "SourceLink", assemblyName, path.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, written?.Invoke(text) ?? text);
            return Task.FromResult<string?>(local);
        }
    }

    /// <summary>A window closed just after its XAML preview was sent its file, whose answer it waits for a minute at most: the
    /// window goes at once, held by nothing that wait leaves (#1445).</summary>
    private void A_window_closed_as_its_preview_waits_for_the_previewer_is_let_go()
    {
        Ui.Collected(Previewed(), "the window closed with its XAML preview to be let go well before the minute the preview waits for its previewer: "
            + "held by that wait, each previewing window would be kept a minute after it closed", TimeSpan.FromSeconds(15));
    }

    /// <summary>The gallery's window shown with its preview, the previewer sent the file, then closed: held only weakly here.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference<MainWindow> Previewed()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Ui.WaitFor(() => preview.Frame is not null, "the previewer's first frame", TimeSpan.FromSeconds(60));
        window.Close();
        Ui.Settle();
        return new WeakReference<MainWindow>(window);
    }

    /// <summary>Checks the frame a preview settled on is its window's design-time width and height drawn at the scale its pane
    /// asked of the previewer: the frame drawn after the first, which came at the window's own scale before the pane knew the
    /// design's size.</summary>
    private static void DrawnAtThePanesScale(Passenger.App.Xaml.XamlPreview preview, Size design)
    {
        double scale = preview.Scaling;
        PixelSize drawn = preview.Frame!.PixelSize;
        Assert.True(Math.Abs(drawn.Width - (design.Width * scale)) < 1 && Math.Abs(drawn.Height - (design.Height * scale)) < 1,
            $"The {design.Width} × {design.Height} window's frame is {drawn.Width} × {drawn.Height} pixels where its pane asked for it at {scale:0.###}: "
            + "the preview settled on a frame of another scale, shrunk or stretched as a picture");
        Assert.True(Math.Abs(preview.FrameSize.Width - design.Width) < 1 && Math.Abs(preview.FrameSize.Height - design.Height) < 1,
            $"The {design.Width} × {design.Height} window's frame is {preview.FrameSize} in the window's units: the pane would show it at another size than its design's");
    }

    private void The_preview_draws_a_window_from_the_last_build_follows_its_text_as_it_is_typed_and_draws_styles_as_their_PreviewWith()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Passenger.App.Editors.EditorGroup group = window.EditorArea.ActiveGroup;
        Assert.True(group.PreviewToggle.IsVisible && group.PreviewToggle.IsChecked == false && ActionTips.Of(group.PreviewToggle) == "XAML Preview",
            $"The XAML file's tab strip offers no XAML Preview toggle (shown {group.PreviewToggle.IsVisible}, tip {ActionTips.Of(group.PreviewToggle)})");
        Ui.Run(window, AppActions.FindAction);
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        popup.Query = "xaml preview";
        Ui.Settle();
        Assert.Equal(AppActions.XamlPreview, popup.Rows[0].Id);
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Assert.True(group.PreviewToggle.IsChecked == true, "The tab strip's preview toggle stayed up while XAML Preview was on");
        Ui.WaitFor(() => preview.Frame is not null, "the previewer's first frame", TimeSpan.FromSeconds(60));
        Ui.Settle();
        Assert.Null(preview.Error);
        DrawnAtThePanesScale(preview, new Size(800, 450));
        Avalonia.Controls.Image frame = window.PreviewPaneOf(document)!.Frame;
        // A point of the panel below its children, where its background shows.
        Color Below() => Ui.Pixel(window, frame.TranslatePoint(new Point(400, 300), window) ?? throw new InvalidOperationException("The preview's frame is not on the window"));
        Color orange = Color.Parse("#E2A33F");
        Assert.NotEqual(orange, Below());

        Ui.PlaceAfter(editor, "<StackPanel");
        window.KeyTextInput(" Background=\"#E2A33F\"");
        Ui.WaitFor(() => Below() == orange, "the preview drawing the panel's new background once typing paused", TimeSpan.FromSeconds(30));

        // The host reads the lone '\r' in the window's tag as a line's end, which the editor reads inside the line.
        Replace(editor, "d:DesignWidth=\"800\" d:DesignHeight", "d:DesignWidth=\"800\"\r d:DesignHeight");
        Replace(editor, "</StackPanel>", "</StackPane>");
        Ui.WaitFor(() => preview.Error is not null, "the previewer saying why the XAML did not load", TimeSpan.FromSeconds(30));
        Assert.Equal(13, editor.Document.Snapshot.GetLineFromOffset(editor.Document.Snapshot.ToString().IndexOf("</StackPane>", StringComparison.Ordinal)));
        Assert.Contains("(line 14, column ", preview.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("15", preview.Error, StringComparison.Ordinal);
        Assert.Equal(orange, Below());
        Replace(editor, "</StackPane>", "</StackPanel>");
        Ui.WaitFor(() => preview.Error is null, "the error gone once the XAML loads again", TimeSpan.FromSeconds(30));

        string styles = app.PathOf("Styles/Buttons.axaml");
        File.WriteAllText(styles, Gallery.Buttons);
        window.OpenFile(styles);
        Ui.Settle();
        OpenDocument shown = window.EditorArea.Active!;
        Passenger.App.Xaml.XamlPreview styled = window.PreviewOf(shown) ?? throw new InvalidOperationException("The styles opened while previewing showed no preview");
        Ui.WaitFor(() => styled.Frame is not null, "the styles' preview", TimeSpan.FromSeconds(60));
        Avalonia.Controls.Image button = window.PreviewPaneOf(shown)!.Frame;
        Ui.WaitFor(() => button.TranslatePoint(new Point(24, styled.FrameSize.Height / 2), window) is { } inside && Ui.Pixel(window, inside) == orange,
            "the button Design.PreviewWith holds drawn in the style's colour", TimeSpan.FromSeconds(30));
        Ui.Click(group.PreviewToggle);
        Ui.Settle();
        Assert.True(window.PreviewOf(shown) is null && group.PreviewToggle.IsChecked == false, "The tab strip's toggle left XAML Preview on");
        window.Close();
    }

    /// <summary>The Avalonia preview places the file's elements as Avalonia's designer draws them, through Passenger's
    /// inspector loaded into it: the element at the caret is outlined from its first placing, a click on an element in the
    /// frame selects its start tag and outlines it, a caret in the panel's tag outlines the panel, and one in the window's
    /// own tag the window.</summary>
    private void An_avalonia_preview_outlines_the_carets_element_and_a_click_on_one_selects_its_start_tag()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Ui.PlaceAfter(editor, "<Button Name=\"G");
        Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Passenger.App.Xaml.XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Passenger.App.Xaml.PreviewElement? Placed(string tag) =>
            preview.Elements.FirstOrDefault(e => e.Tag.Start == editor.Document.Snapshot.ToString().IndexOf(tag, StringComparison.Ordinal));
        Ui.WaitFor(() => Placed("<Button Name=\"Go\"") is { } placed && pane.Outlined == placed.Box,
            () => $"the Go button outlined at the caret once the preview placed its elements ({preview.Status}; {preview.Elements.Count} placed)", TimeSpan.FromSeconds(60));
        Assert.True(Placed("<Button Name=\"Go\"")!.Box is { Width: > 0, Height: > 0 }, $"The Go button was placed at {Placed("<Button Name=\"Go\"")!.Box}");

        Passenger.App.Xaml.PreviewElement title = Placed("<TextBlock Name=\"Title\"") ?? throw new InvalidOperationException("The preview placed no Title");
        Point at = pane.Frame.TranslatePoint(new Point(title.Box.X + 4, title.Box.Center.Y), window) ?? throw new InvalidOperationException("The preview's frame is not on the window");
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
        string text = editor.Document.Snapshot.ToString();
        int start = text.IndexOf("<TextBlock Name=\"Title\"", StringComparison.Ordinal);
        int end = text.IndexOf("/>", start, StringComparison.Ordinal) + 2;
        Assert.Equal(new Selection(end, start), editor.Selections.Primary with { PreferredX = double.NaN });
        Assert.Equal(title.Box, pane.Outlined);

        Ui.PlaceAfter(editor, "<StackPanel Margin");
        Ui.Settle();
        Assert.Equal(Placed("<StackPanel Margin")?.Box, pane.Outlined);
        Ui.PlaceAfter(editor, "<Wind");
        Ui.Settle();
        Assert.True(Placed("<Window") is { } root && pane.Outlined == root.Box && root.Box.Width > 0, $"The window's tag outlined {pane.Outlined}, not the window");
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>In the XAML workspace, with no app running, the preview shows under its frame what the caret's element sets
    /// itself, as its XAML says it: the title's binding with its path, the button's own content and its resource with its key,
    /// and the Name each takes as a property; turned off, it shows none.</summary>
    private void In_the_xaml_workspace_the_preview_shows_what_the_carets_element_sets_itself_while_no_app_runs()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.PlaceAfter(editor, "<TextBlock Name=\"T");
        window.ApplySettings(window.Settings with { XamlWorkspace = true });
        Ui.WaitFor(() => window.PreviewPaneOf(document) is { PropertiesShown.Element: "TextBlock #Title" },
            () => $"the title's own values under the preview, where it shows {window.PreviewPaneOf(document)?.PropertiesShown.Element}");
        Passenger.App.Xaml.XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Assert.Equal([("Name", "Title", "Local"), ("Text", "{Binding Greeting}", "Binding · Greeting")], pane.PropertiesShown.Values);

        Ui.PlaceAfter(editor, "<Button Name=\"G");
        Ui.WaitFor(() => pane.PropertiesShown.Element == "Button #Go", () => $"the button's own values under the preview, not {pane.PropertiesShown.Element}'s");
        Assert.Equal([("Name", "Go", "Local"), ("Content", "Go", "Local"), ("Background", "{StaticResource Accent}", "Resource · Accent")], pane.PropertiesShown.Values);

        window.ApplySettings(window.Settings with { XamlWorkspace = false });
        Ui.Settle();
        Assert.Null(pane.PropertiesShown.Element);
        if (window.PreviewOf(document) is not null)
        {
            Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        }
        window.Close();
    }

    /// <summary>An Avalonia preview draws its root at a phone's and a tablet's size beside its design's (#1549): chosen
    /// beside the preview's title, the frame takes the size chosen as the root lays itself out there, the file's text stays as
    /// written, and Design size brings the design's back.</summary>
    private void An_avalonia_preview_draws_its_root_at_a_phones_and_a_tablets_size_beside_its_design_size()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        OpenDocument document = window.EditorArea.Active!;
        string written = document.Text.Snapshot.ToString();
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Passenger.App.Xaml.XamlPreviewPane pane = window.PreviewPaneOf(document)!;
        Ui.WaitFor(() => preview.Frame is not null && preview.FrameSize.Width > 0, () => $"the previewer's first frame; the preview says: {preview.Status} {preview.Error}",
            TimeSpan.FromSeconds(60));
        Size design = preview.FrameSize;
        Assert.True(pane.Sizes.IsVisible, "The preview of a window offers no sizes beside its design's");
        Assert.Equal([Passenger.App.Xaml.XamlPreviewPane.DesignSize, "Phone (412 × 915)", "Phone, turned (915 × 412)", "Tablet (800 × 1280)", "Tablet, turned (1280 × 800)"],
            pane.Sizes.Items.Cast<string>());
        foreach (int chosen in (int[])[1, 3, 0])
        {
            pane.Sizes.SelectedIndex = chosen;
            Size wanted = chosen == 0 ? design : new Size(Passenger.Xaml.Features.PreviewSize.Offered[chosen - 1].Width, Passenger.Xaml.Features.PreviewSize.Offered[chosen - 1].Height);
            Ui.WaitFor(() => DrawnAt(preview, wanted), () => $"the frame at {pane.Sizes.SelectedItem}; it is {preview.FrameSize} in its units, {preview.Frame?.PixelSize} pixels drawn at "
                + $"{preview.Scaling:0.####} in the pane {pane.Bounds.Size}: {preview.Status} {preview.Error}", TimeSpan.FromSeconds(60));
            Assert.Null(preview.Error);
        }
        Assert.Equal(written, document.Text.Snapshot.ToString());
        // XAML Preview is kept on for the next window, which the class's other tests open with it off.
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>Whether the frame is a root of this size in the window's units drawn at the scale it was: a frame drawn
    /// smaller for a narrow pane is whole pixels, and Avalonia rounds its layout up to whole pixels, so it stands up to two
    /// pixels past the size times its scale, which read back in units is several units at a small scale.</summary>
    internal static bool DrawnAt(Passenger.App.Xaml.XamlPreview preview, Size wanted) =>
        preview.Frame is { } frame && preview.FrameSize.Width > 0 && preview.FrameSize.Height > 0
        && Math.Abs(frame.PixelSize.Width - (wanted.Width * frame.PixelSize.Width / preview.FrameSize.Width)) <= 2
        && Math.Abs(frame.PixelSize.Height - (wanted.Height * frame.PixelSize.Height / preview.FrameSize.Height)) <= 2;

    /// <summary>An app still on Avalonia 11, whose package carries its previewer under tools/netstandard2.0 where Avalonia
    /// 12's are under tools/net8.0 and later, and whose Directory.Build.props moves its obj and bin folders, previews as
    /// any app does: turned on from its file's tab strip before the app was ever restored, it draws once Build has
    /// restored and built it.</summary>
    private void An_app_on_Avalonia_11_with_its_obj_and_bin_folders_moved_previews_from_its_packages_previewer()
    {
        string folder = TestFolders.Create("avalonia11-");
        string version = AvaloniaApp.Metadata("AvaloniaOlderVersion");
        TestFolders.Write(folder, "Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)build/obj/</BaseIntermediateOutputPath>
                <BaseOutputPath>$(MSBuildThisFileDirectory)build/bin/</BaseOutputPath>
              </PropertyGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Eleven.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>{{TestProjects.Framework}}</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Avalonia" Version="{{version}}" />
                <PackageReference Include="Avalonia.Desktop" Version="{{version}}" />
                <PackageReference Include="Avalonia.Themes.Fluent" Version="{{version}}" />
              </ItemGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Program.cs", """
            using Avalonia;

            namespace Eleven;

            internal static class Program
            {
                [System.STAThread]
                public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

                public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
            }
            """);
        TestFolders.Write(folder, "App.axaml", """
            <Application xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Eleven.App">
                <Application.Styles>
                    <FluentTheme />
                </Application.Styles>
            </Application>
            """);
        TestFolders.Write(folder, "App.axaml.cs", """
            using Avalonia;
            using Avalonia.Controls.ApplicationLifetimes;
            using Avalonia.Markup.Xaml;

            namespace Eleven;

            public partial class App : Application
            {
                public override void Initialize() => AvaloniaXamlLoader.Load(this);

                public override void OnFrameworkInitializationCompleted()
                {
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.MainWindow = new MainWindow();
                    }
                    base.OnFrameworkInitializationCompleted();
                }
            }
            """);
        TestFolders.Write(folder, "MainWindow.axaml", """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    mc:Ignorable="d" d:DesignWidth="400" d:DesignHeight="300" x:Class="Eleven.MainWindow" Title="Eleven">
                <StackPanel Background="#E2A33F">
                    <TextBlock Text="On Avalonia 11" />
                </StackPanel>
            </Window>
            """);
        TestFolders.Write(folder, "MainWindow.axaml.cs", """
            using Avalonia.Controls;

            namespace Eleven;

            public partial class MainWindow : Window
            {
                public MainWindow() => InitializeComponent();
            }
            """);
        AvaloniaApp.UseFeed(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Assert.Null(window.OpenFile(Path.Combine(folder, "MainWindow.axaml")));
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Passenger.App.Editors.EditorGroup group = window.EditorArea.ActiveGroup;
        Ui.Click(group.PreviewToggle);
        Ui.Settle();
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("The tab strip's toggle showed no preview beside the file");
        Assert.StartsWith("Build Eleven to preview its XAML", preview.Status, StringComparison.Ordinal);
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Ui.WaitFor(() => preview.Frame is not null, () => $"the Avalonia 11 previewer's first frame (the pane says: {preview.Status})", TimeSpan.FromSeconds(60));
        Ui.Settle();
        Assert.Null(preview.Error);
        DrawnAtThePanesScale(preview, new Size(400, 300));
        Avalonia.Controls.Image frame = window.PreviewPaneOf(document)!.Frame;
        Point inside = frame.TranslatePoint(new Point(frame.Bounds.Width / 2, frame.Bounds.Height * 0.75), window) ?? throw new InvalidOperationException("The preview's frame is not on the window");
        Assert.Equal(Color.Parse("#E2A33F"), Ui.Pixel(window, inside));
        window.Close();
    }

    /// <summary>The XAML preview of a view whose previewer writes to its output and its errors without pause, opened and
    /// closed twenty times beside two busy processes (each other time closed as soon as its previewer has started, else
    /// once it has drawn), leaves the IDE with no error caught: no read of the previewer's output faults as it ends, nor
    /// is left faulted for the garbage collector to find.</summary>
    private void Opening_and_closing_the_XAML_preview_twenty_times_beside_busy_processes_faults_no_read_of_the_previewer()
    {
        app.Write("Chatty.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.Chatty" Width="200" Height="100">
                <TextBlock Text="Chatty" />
            </UserControl>

            """);
        app.Write("Chatty.axaml.cs", """
            using System;
            using System.Threading;
            using Avalonia.Controls;

            namespace Gallery;

            public partial class Chatty : UserControl
            {
                public Chatty()
                {
                    InitializeComponent();
                    if (Design.IsDesignMode)
                    {
                        // A line a millisecond on the previewer's output and its errors, for as long as it runs.
                        new Thread(() =>
                        {
                            for (long line = 0; ; line++)
                            {
                                Console.WriteLine($"chatty {line}");
                                Console.Error.WriteLine($"chatty {line}");
                                Thread.Sleep(1);
                            }
                        }) { IsBackground = true }.Start();
                    }
                }
            }

            """);
        MainWindow window = app.Open(app.PathOf("Chatty.axaml"), languageServer: false);
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Passenger.App.Editors.EditorGroup group = window.EditorArea.ActiveGroup;
        OpenDocument document = window.EditorArea.Active!;
        int previewers = 0;
        void OnStarted(object? sender, ChildStart start)
        {
            if (start.Command.Contains("Avalonia.Designer.HostApp", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref previewers);
            }
        }
        List<System.Diagnostics.Process> busy = ChildOutputTests.Busy();
        ChildProcesses.Started += OnStarted;
        try
        {
            for (int round = 1; round <= 20; round++)
            {
                Ui.Click(group.PreviewToggle);
                Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException($"Round {round}: the toggle showed no preview");
                if (round % 2 == 1)
                {
                    Ui.PumpUntil(() => Volatile.Read(ref previewers) == round, $"round {round}'s previewer to start");
                }
                else
                {
                    Ui.WaitFor(() => preview.Frame is not null, $"round {round}'s previewer to draw the view ({preview.Status})", TimeSpan.FromSeconds(60));
                }
                Ui.Click(group.PreviewToggle);
                Ui.Settle();
                Assert.True(window.PreviewOf(document) is null, $"Round {round}: the toggle left the preview on");
            }
        }
        finally
        {
            ChildProcesses.Started -= OnStarted;
            ChildOutputTests.End(busy);
        }
        Assert.Equal(20, Volatile.Read(ref previewers));
        // A read left faulted is told only as its task is collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Ui.Settle();
        Assert.True(IdeErrors.Errors.Count == 0, $"Ending the previewer twenty times left the IDE with errors caught: {string.Join("; ", IdeErrors.Errors.Select(e => $"{e.Kind}: {e.Message}"))}");
        window.Close();
    }

    /// <summary>The XAML preview of a view whose previewer writes more than a pipe holds and then a last line, and exits at
    /// once, says the previewer ended with that last line, each of three times beside two busy processes: what it wrote is
    /// read to its end before it is quoted, never "it said nothing" or an earlier line.</summary>
    private void A_previewer_that_writes_a_last_line_and_exits_at_once_is_said_to_have_ended_with_that_line()
    {
        app.Write("Ending.axaml", """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.Ending" Width="200" Height="100">
                <TextBlock Text="Ending" />
            </UserControl>

            """);
        app.Write("Ending.axaml.cs", """
            using System;
            using Avalonia.Controls;

            namespace Gallery;

            public partial class Ending : UserControl
            {
                public Ending()
                {
                    InitializeComponent();
                    if (Design.IsDesignMode)
                    {
                        for (int line = 0; line < 2000; line++)
                        {
                            Console.Out.Write($"{line:D5} {new string('w', 58)}\n");
                        }
                        Console.Out.Write("the previewer's last words\n");
                        Console.Out.Flush();
                        Environment.Exit(3);
                    }
                }
            }

            """);
        MainWindow window = app.Open(app.PathOf("Ending.axaml"), languageServer: false);
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Passenger.App.Editors.EditorGroup group = window.EditorArea.ActiveGroup;
        OpenDocument document = window.EditorArea.Active!;
        List<System.Diagnostics.Process> busy = ChildOutputTests.Busy();
        try
        {
            for (int round = 1; round <= 3; round++)
            {
                Ui.Click(group.PreviewToggle);
                Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException($"Round {round}: the toggle showed no preview");
                Ui.WaitFor(() => preview.Status.StartsWith("The previewer ended ", StringComparison.Ordinal),
                    () => $"round {round}'s previewer to end, the pane saying so, not '{preview.Status}'", TimeSpan.FromSeconds(60));
                Assert.Equal("The previewer ended with exit code 3: the previewer's last words", preview.Status);
                Ui.Click(group.PreviewToggle);
                Ui.Settle();
            }
        }
        finally
        {
            ChildOutputTests.End(busy);
        }
        window.Close();
    }

    /// <summary>An Avalonia previewer that ends as it draws (a crash, here killed) is let go of: the pane says its exit code
    /// and last words, which passenger.log has too, as the WPF previewer's end, and the next edit starts a new previewer,
    /// whose frame draws the edit.</summary>
    private void An_Avalonia_previewer_that_ends_says_its_exit_and_the_next_edit_draws_in_a_new_one()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        List<int> previewers = [];
        void OnStarted(object? sender, ChildStart start)
        {
            if (start.Command.Contains("Avalonia.Designer.HostApp", StringComparison.Ordinal))
            {
                lock (previewers)
                {
                    previewers.Add(start.ProcessId);
                }
            }
        }
        int[] Previewers()
        {
            lock (previewers)
            {
                return [.. previewers];
            }
        }
        ChildProcesses.Started += OnStarted;
        try
        {
            Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
            Ui.Settle();
            Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
            Ui.WaitFor(() => preview.Frame is not null, "the previewer's first frame", TimeSpan.FromSeconds(60));
            int killed = Assert.Single(Previewers());

            OwnProcess.Of(killed).Kill(alone: true);
            Ui.WaitFor(() => preview.Status.StartsWith("The previewer ended", StringComparison.Ordinal), () => $"the pane saying the previewer ended, not '{preview.Status}'");
            // Process.Kill ends it with TerminateProcess's -1 on Windows, and with SIGKILL on macOS and Linux.
            string exit = OperatingSystem.IsWindows() ? "exit code -1 (0xFFFFFFFF)" : "exit code 137 (SIGKILL)";
            string ended = $"The previewer ended with {exit}: ";
            Assert.True(preview.Status.StartsWith(ended, StringComparison.Ordinal), $"The pane said '{preview.Status}', not how the previewer ended ('{ended}')");
            Assert.False(preview.Running, "The preview kept the previewer that ended as its own");
            string last = preview.Status[ended.Length..];
            IdeErrors.Flush();
            string log = Ui.Shared(IdeErrors.LogPath);
            string? line = log.Split('\n').LastOrDefault(l => l.Contains($"The Avalonia previewer of MainWindow.axaml ended with {exit}: ", StringComparison.Ordinal));
            Assert.True(line is not null && line.Contains(last, StringComparison.Ordinal),
                $"passenger.log has {(line is null ? "nothing of the Avalonia previewer's end" : $"its end as '{line}'")}, where the pane says it ended with '{last}'");

            Avalonia.Controls.Image frame = window.PreviewPaneOf(document)!.Frame;
            // A point of the panel below its children, where its background shows.
            Color Below() => Ui.Pixel(window, frame.TranslatePoint(new Point(400, 300), window) ?? throw new InvalidOperationException("The preview's frame is not on the window"));
            Color orange = Color.Parse("#E2A33F");
            Assert.NotEqual(orange, Below());
            Ui.PlaceAfter(editor, "<StackPanel");
            window.KeyTextInput(" Background=\"#E2A33F\"");
            Ui.WaitFor(() => Previewers().Length == 2, () => $"a new previewer started by the edit after the last ended (the pane says: {preview.Status})", TimeSpan.FromSeconds(30));
            Ui.WaitFor(() => Below() == orange, () => $"the new previewer drawing the panel's new background (the pane says: {preview.Status})", TimeSpan.FromSeconds(60));
            Assert.True(preview.Running && preview.Status == "Live", $"The new previewer is not live: the pane says '{preview.Status}'");
        }
        finally
        {
            ChildProcesses.Started -= OnStarted;
        }
        window.Close();
    }

    /// <summary>A build after one Avalonia's XAML compiler failed, nothing changed since, says the app's build has no compiled
    /// XAML and offers Rebuild Solution over the Build window's output, where it said it succeeded (#2043): it took the
    /// assembly the C# compiler wrote before that failure as up to date and copied it out. The preview of that build says so
    /// in the same words, offering the rebuild, where it said its previewer ended with exit code 134; the XAML put right,
    /// the rebuild it offers compiles it and the preview draws.</summary>
    private void A_build_after_a_failed_xaml_compile_says_its_xaml_was_never_compiled_and_the_preview_offers_the_rebuild()
    {
        string xaml = app.Write("MainWindow.axaml", Gallery.Window.Replace("<TextBlock Name=\"Title\"", "<TextBlock Name=\"Title\" Width=\"wide\"", StringComparison.Ordinal));
        MainWindow window = app.Open(xaml, languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        Assert.True(window.BottomPanel.Problems.Problems.Any(p => p.Code == "AVLN1000"),
            $"The first build did not fail in Avalonia's XAML compiler: {window.Status}; {string.Join("; ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code}: {p.Message}"))}");

        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        string uncompiled = Refusals.XamlUncompiled(["Gallery"]).Shown;
        Assert.True(window.Status == uncompiled, $"The build after it, nothing changed, said '{window.Status}', not that the app's XAML was never compiled");
        Passenger.App.ToolWindows.OutputView console = window.BottomPanel.Build;
        Assert.True(console.Offered == uncompiled && console.OfferLink == "Rebuild Solution",
            $"The Build window offers {console.OfferLink ?? "nothing"} ('{console.Offered}'), not Rebuild Solution for the XAML never compiled");

        Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
        Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document) ?? throw new InvalidOperationException("XAML Preview showed no preview beside the file");
        Ui.WaitFor(() => preview.Status == uncompiled, () => $"the preview saying its build's XAML was never compiled, not '{preview.Status}'", TimeSpan.FromSeconds(60));
        Avalonia.Controls.Button rebuild = window.PreviewPaneOf(document)!.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .SingleOrDefault(b => b.IsEffectivelyVisible && b.Content as string == "Rebuild Solution")
            ?? throw new InvalidOperationException($"The preview offers no Rebuild Solution under '{preview.Status}' (it offers {preview.Offers ?? "nothing"})");

        Replace(editor, " Width=\"wide\"", string.Empty);
        Ui.Click(rebuild);
        Ui.WaitFor(() => preview.Frame is not null && preview.Error is null, () => $"the preview drawing the rebuilt app, not '{preview.Status}' {preview.Error} (the status bar: {window.Status})",
            TimeSpan.FromMinutes(2));
        Assert.True(window.Status?.StartsWith("Rebuild succeeded", StringComparison.Ordinal) == true, $"The rebuild the preview offered said '{window.Status}'");
        Assert.Null(console.Offered);
        // XAML Preview is kept on for the next window, which the class's other tests open with it off.
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>A previewer is sent nothing until it says its session started, after setting itself up: what it is told
    /// while it sets up it holds until it listens, then hands over in a race that may lose a message or give it out of order.
    /// A stand-in on the preview's port hears nothing as it connects, then the formats, the scale and the file once it says
    /// its session started, and the frame it answers with is drawn.</summary>
    private void A_previewer_is_sent_nothing_until_it_says_its_session_started()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"), languageServer: false);
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        using (StandInPreviewer standIn = StandInPreviewer.InPlaceOf(window, document))
        {
            Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document)!;
            Ui.Hold(TimeSpan.FromSeconds(1), "what is sent as a previewer connects to reach it");
            Assert.True(standIn.Heard.Count == 0, $"Passenger sent the previewer {string.Join(", ", standIn.Heard.Select(m => m.GetType().Name))} before it said its "
                + "session started, which a previewer still setting itself up holds and hands over in a race that may lose one or give it out of order");
            standIn.Begin();
            Ui.WaitFor(() => preview.Frame is not null && Math.Abs(preview.FrameSize.Width - StandInPreviewer.WidthOf(1)) < 0.5,
                () => $"the stand-in's frame drawn, where the preview says '{preview.Status}' and the stand-in heard {string.Join(", ", standIn.Heard.Select(m => m.GetType().Name))}");
            Assert.Equal([nameof(Avalonia.Remote.Protocol.Viewport.ClientSupportedPixelFormatsMessage), nameof(Avalonia.Remote.Protocol.Viewport.ClientRenderInfoMessage),
                nameof(Avalonia.Remote.Protocol.Designer.UpdateXamlMessage)], standIn.Heard.Take(3).Select(m => m.GetType().Name));
        }
        // XAML Preview is kept on for the next window, which the class's other tests open with it off.
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>A preview holds screenshots and tests until the frame of the text it last sent, never one of the window that
    /// text's load replaces, which a previewer may draw as the load comes: a stand-in meeting an edit's load with a frame of
    /// its last window, then answering it a moment later, has its new window's frame shown once the window settles.</summary>
    private void An_edits_preview_settles_on_its_own_frame_never_one_of_the_window_its_load_replaces()
    {
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"), languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        using (StandInPreviewer standIn = StandInPreviewer.InPlaceOf(window, document))
        {
            Passenger.App.Xaml.XamlPreview preview = window.PreviewOf(document)!;
            standIn.Begin();
            Ui.WaitFor(() => preview.Frame is not null && Math.Abs(preview.FrameSize.Width - StandInPreviewer.WidthOf(1)) < 0.5,
                () => $"the stand-in's first window drawn, where the preview says '{preview.Status}'");
            Ui.Settle();

            Ui.PlaceAfter(editor, "<StackPanel");
            window.KeyTextInput(" ");
            Ui.Settle();
            Assert.True(standIn.Loads == 2, $"The edit was sent as load {standIn.Loads}, not the second");
            Assert.True(Math.Abs(preview.FrameSize.Width - StandInPreviewer.WidthOf(2)) < 0.5 && preview.Status == "Live",
                $"The window settled on a frame {preview.FrameSize.Width} wide ('{preview.Status}'), the replaced window's, not the {StandInPreviewer.WidthOf(2)} of the edit's own: "
                + "a screenshot or a test taken then shows the text before the edit");
        }
        Assert.True(window.Perform(AppActions.XamlPreview) && window.PreviewOf(document) is null, "XAML Preview stayed on");
        window.Close();
    }

    /// <summary>A previewer of the test's own on the preview's port, the real one stopped as it starts: what Passenger sends
    /// it is kept, and it answers each load as Avalonia's previewer does, with a frame of a new window as wide as the load's
    /// number says (<see cref="WidthOf"/>), drawn at the scale last asked; a load after the first it meets first with a frame
    /// of the window the load replaces, as one drawn before the load reached it, and answers a moment later.</summary>
    private sealed class StandInPreviewer : IDisposable
    {
        /// <summary>How long a load after the first waits for its answer, after the replaced window's frame.</summary>
        private static readonly TimeSpan Replacing = TimeSpan.FromSeconds(2);

        private readonly Avalonia.Remote.Protocol.IAvaloniaRemoteTransportConnection connection;
        private readonly List<object> heard = [];
        private readonly List<Task> sent = [];
        private int loads;
        private long sequence;
        private double dpi = 96;

        private StandInPreviewer(Avalonia.Remote.Protocol.IAvaloniaRemoteTransportConnection connection)
        {
            this.connection = connection;
            connection.OnMessage += (_, message) => Answer(message);
        }

        /// <summary>What Passenger sent, in order.</summary>
        public IReadOnlyList<object> Heard
        {
            get
            {
                lock (heard)
                {
                    return [.. heard];
                }
            }
        }

        /// <summary>The loads Passenger has sent.</summary>
        public int Loads => Volatile.Read(ref loads);

        /// <summary>The width, in units, of the window a load makes.</summary>
        public static int WidthOf(int load) => 80 + (40 * load);

        /// <summary>Turns XAML Preview on, stops the previewer it starts as it starts, and connects in its place; the loop run
        /// without settling meanwhile, as the preview holds the window for the frame only the stand-in will draw.</summary>
        public static StandInPreviewer InPlaceOf(MainWindow window, OpenDocument document)
        {
            int port = 0;
            void OnStarted(object? sender, ChildStart start)
            {
                if (start.Command.Contains("Avalonia.Designer.HostApp", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.Match(start.Command, @"tcp-bson://127\.0\.0\.1:(\d+)/") is { Success: true } named)
                {
                    OwnProcess.Of(start.ProcessId).Suspend("The previewer");
                    Volatile.Write(ref port, int.Parse(named.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            ChildProcesses.Started += OnStarted;
            try
            {
                Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
                Ui.PumpUntil(() => Volatile.Read(ref port) != 0, "the previewer started");
            }
            finally
            {
                ChildProcesses.Started -= OnStarted;
            }
            Avalonia.Remote.Protocol.IAvaloniaRemoteTransportConnection? made = null;
            Ui.AwaitPumped(Waiting.Apart(async () => made = await new Avalonia.Remote.Protocol.BsonTcpTransport().Connect(System.Net.IPAddress.Loopback, port)),
                "the stand-in's connection to the preview's port");
            return new StandInPreviewer(made!);
        }

        /// <summary>Says its session started, as Avalonia's previewer does once it has set itself up.</summary>
        public void Begin() => Send(new Avalonia.Remote.Protocol.Designer.StartDesignerSessionMessage { SessionId = "stand-in" });

        /// <summary>On the connection's thread, in the order Passenger sent them.</summary>
        private void Answer(object message)
        {
            lock (heard)
            {
                heard.Add(message);
            }
            switch (message)
            {
                case Avalonia.Remote.Protocol.Viewport.ClientRenderInfoMessage info:
                    dpi = info.DpiX;
                    if (Loads > 0)
                    {
                        Send(Frame(Loads));
                    }
                    break;
                case Avalonia.Remote.Protocol.Designer.UpdateXamlMessage:
                    int load = Interlocked.Increment(ref loads);
                    if (load > 1)
                    {
                        Send(Frame(load - 1));
                        Waiting.For(Replacing, "the replaced window's frame heard before the load is answered");
                    }
                    Send(new Avalonia.Remote.Protocol.Designer.UpdateXamlResultMessage());
                    Send(Frame(load));
                    break;
            }
        }

        private Avalonia.Remote.Protocol.Viewport.FrameMessage Frame(int load)
        {
            int width = (int)Math.Ceiling(WidthOf(load) * dpi / 96);
            int height = (int)Math.Ceiling(40 * dpi / 96);
            return new Avalonia.Remote.Protocol.Viewport.FrameMessage
            {
                SequenceId = Interlocked.Increment(ref sequence),
                Format = Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888,
                Width = width,
                Height = height,
                Stride = width * 4,
                Data = new byte[width * 4 * height],
                DpiX = dpi,
                DpiY = dpi,
            };
        }

        private void Send(object message)
        {
            lock (sent)
            {
                sent.Add(connection.Send(message));
            }
        }

        public void Dispose()
        {
            Task[] sending;
            lock (sent)
            {
                sending = [.. sent];
            }
            Ui.Await(Task.WhenAll(sending), "what the stand-in sent");
            connection.Dispose();
        }
    }

    /// <summary>With XAML Preview on, previewers run only for the preview shown and the one hidden last: three views shown in
    /// turn leave two previewers, the first's ended; the one hidden last, shown again, draws with the previewer it kept; a
    /// build that changes nothing starts none again (on Windows, where a previewer holds the build it loaded, it ends both
    /// and starts the shown view's again), and one after an edit to the view shown builds and starts its previewer alone
    /// again, ending the hidden one's, which starts again as its view is shown.</summary>
    private void Previewers_run_for_the_preview_shown_and_the_one_hidden_last_and_a_build_starts_only_the_shown_one_again()
    {
        app.Write("One.axaml", Panel("One", "#3D7BF5"));
        app.Write("One.axaml.cs", PanelCode("One"));
        app.Write("Two.axaml", Panel("Two", "#2E9E5B"));
        app.Write("Two.axaml.cs", PanelCode("Two"));
        MainWindow window = app.Open(app.PathOf("MainWindow.axaml"), languageServer: false);
        Ui.Run(window, AppActions.Build);
        Ui.Settle();
        OpenDocument main = window.EditorArea.Active!;
        List<int> previewers = [];
        void OnStarted(object? sender, ChildStart start)
        {
            if (start.Command.Contains("Avalonia.Designer.HostApp", StringComparison.Ordinal))
            {
                lock (previewers)
                {
                    previewers.Add(start.ProcessId);
                }
            }
        }
        int[] Started()
        {
            lock (previewers)
            {
                return [.. previewers];
            }
        }
        ChildProcesses.Started += OnStarted;
        try
        {
            Assert.True(window.Perform(AppActions.XamlPreview), $"XAML Preview could not run: {window.Status}");
            Drawn(main, "the window");
            OpenDocument one = Shown("One.axaml");
            OpenDocument two = Shown("Two.axaml");
            Assert.True(Started().Length == 3, $"Three views shown in turn started {Plural.Of(Started().Length, "previewer")}");
            Ui.WaitFor(() => Ended(Started()[0]), "the window's previewer to end once two views were shown after it");
            Assert.True(!Preview(main).Running && Preview(one).Running && Preview(two).Running,
                $"The previewers running are not the shown view's and the one hidden last's (window {Preview(main).Running}, One {Preview(one).Running}, Two {Preview(two).Running})");

            Assert.Null(window.OpenFile(app.PathOf("One.axaml")));
            Ui.Settle();
            Assert.True(Started().Length == 3 && Preview(one).Running && Preview(one).Status == "Live",
                $"One, hidden last, was shown again with {Plural.Of(Started().Length - 3, "new previewer")}, its pane saying '{Preview(one).Status}'");

            Ui.Run(window, AppActions.Build);
            Ui.Settle();
            bool holds = OperatingSystem.IsWindows();
            Assert.True(Started().Length == (holds ? 4 : 3) && Preview(one).Running && Preview(two).Running != holds,
                $"A build that changed nothing started {Plural.Of(Started().Length - 3, "previewer")} again (One running {Preview(one).Running}, Two {Preview(two).Running}), "
                + $"the status bar saying '{window.Status}'");

            // An edit the build compiles into the project's assembly, which the shown view's previewer then draws from.
            Replace(window.EditorArea.CodeEditor, "#3D7BF5", "#E2A33F");
            int before = Started().Length;
            Ui.Run(window, AppActions.Build);
            Ui.Settle();
            Assert.True(Started().Length == before + 1,
                $"A build started {Plural.Of(Started().Length - before, "previewer")} again, where only the shown view's should start again, the status bar saying '{window.Status}'");
            Assert.True(Preview(one).Running && Preview(one).Status == "Live", $"One's new previewer is not live: the pane says '{Preview(one).Status}'");
            Assert.True(!Preview(two).Running, "The build left the hidden view's previewer running on the build before it");
            Ui.WaitFor(() => Ended(Started()[1]), "One's previewer of the build before to end with the build");
            Ui.WaitFor(() => Ended(Started()[2]), "Two's previewer to end with the build, its view hidden");

            before = Started().Length;
            Assert.Equal(two, Shown("Two.axaml"));
            Assert.True(Started().Length == before + 1, $"Two, shown again after its previewer ended, started {Plural.Of(Started().Length - before, "previewer")}");
            Assert.True(Preview(one).Running && !Preview(main).Running, "Two shown again did not leave One's previewer running alone beside its own");
        }
        finally
        {
            ChildProcesses.Started -= OnStarted;
        }
        window.Close();

        Passenger.App.Xaml.XamlPreview Preview(OpenDocument document) =>
            window.PreviewOf(document) ?? throw new InvalidOperationException($"{Path.GetFileName(document.Path)} has no preview kept");

        // A view shown in the editor, its preview drawn.
        OpenDocument Shown(string name)
        {
            Assert.Null(window.OpenFile(app.PathOf(name)));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Drawn(document, name);
            return document;
        }

        void Drawn(OpenDocument document, string what)
        {
            Ui.Settle();
            Passenger.App.Xaml.XamlPreview preview = Preview(document);
            Ui.WaitFor(() => preview.Frame is not null && preview.Status == "Live", () => $"{what}'s preview drawn (the pane says: {preview.Status})", TimeSpan.FromSeconds(60));
        }

        // A previewer Passenger started that has since ended.
        static bool Ended(int id) => !DotnetProcesses.Runs(id);
    }

    /// <summary>A view of the gallery's, filled with a colour.</summary>
    private static string Panel(string name, string colour) => $$"""
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="Gallery.{{name}}" Width="200" Height="100">
            <Border Background="{{colour}}">
                <TextBlock Text="{{name}}" />
            </Border>
        </UserControl>

        """;

    private static string PanelCode(string name) => $$"""
        using Avalonia.Controls;

        namespace Gallery;

        public partial class {{name}} : UserControl
        {
            public {{name}}() => InitializeComponent();
        }

        """;

    internal static Marker MarkerOn(CodeEditor editor, OpenDocument document, string stretch)
    {
        string text = editor.Document.Snapshot.ToString();
        return Assert.Single(document.Markers, m => m.Range.End <= text.Length && text[m.Range.Start..m.Range.End] == stretch);
    }

    internal static void Replace(CodeEditor editor, string what, string with)
    {
        int at = editor.Document.Snapshot.ToString().IndexOf(what, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{what}' is not in the file");
        editor.ApplyEdits([new TextEdit(at, what.Length, with)]);
    }

    /// <summary>The repository the tests were built from.</summary>
    internal static string Repository
    {
        get
        {
            for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
            {
                if (File.Exists(Path.Combine(at.FullName, "Passenger.slnx")))
                {
                    return at.FullName;
                }
            }
            throw new DirectoryNotFoundException($"No folder above {AppContext.BaseDirectory} holds Passenger.slnx");
        }
    }

    /// <summary>The caret's line with a bar where the caret is.</summary>
    internal static string CaretLine(CodeEditor editor) =>
        editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).Insert(editor.CaretPosition.Column, "|");

    /// <summary>The app: Avalonia's template with a view model its window binds through <c>x:DataType</c>, a palette of
    /// brushes in a theme dictionary merged in by App.axaml, and an asset.</summary>
    public sealed class Gallery : SharedProject
    {
        public Gallery()
            : base("xaml-")
        {
        }

        public override async ValueTask InitializeAsync()
        {
            await TestFolders.PrepareAsync(() => MakeAsync(Folder), Folder);
            await PrepareAsync();
        }

        /// <summary>Makes the app in a folder, not yet restored.</summary>
        internal static async Task MakeAsync(string folder)
        {
            await AvaloniaApp.CreateAsync(folder, "Gallery");
            // Its App.axaml takes Fluent's theme, whose own XAML the tests go to, where the template's takes Fluent 2's.
            string project = Path.Combine(folder, "Gallery.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("\"Avalonia.Themes.Fluent2\"", "\"Avalonia.Themes.Fluent\"", StringComparison.Ordinal));
            Directory.CreateDirectory(Path.Combine(folder, "ViewModels"));
            Directory.CreateDirectory(Path.Combine(folder, "Styles"));
            Directory.CreateDirectory(Path.Combine(folder, "Assets"));
            File.WriteAllText(Path.Combine(folder, "ViewModels/MainViewModel.cs"), MainViewModel);
            File.WriteAllText(Path.Combine(folder, "Styles/Palette.axaml"), Palette);
            File.WriteAllBytes(Path.Combine(folder, "Assets/logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            File.WriteAllText(Path.Combine(folder, "App.axaml"), AppXaml);
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml"), Window);
            File.WriteAllText(Path.Combine(folder, "MainWindow.axaml.cs"), CodeBehind);
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
        }

        public const string MainViewModel = """
            namespace Gallery.ViewModels;

            public class MainViewModel
            {
                public string Greeting { get; set; } = "Hello";

                public Person Owner { get; } = new();

                public int Count { get; set; }
            }

            public class Person
            {
                public string Name { get; set; } = "Ada";

                public int Age { get; set; }
            }

            """;

        public const string Palette = """
            <ResourceDictionary xmlns="https://github.com/avaloniaui"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="Dark">
                        <SolidColorBrush x:Key="Accent" Color="#3D7BF5" />
                        <SolidColorBrush x:Key="Muted" Color="#868A91" />
                    </ResourceDictionary>
                    <ResourceDictionary x:Key="Light">
                        <SolidColorBrush x:Key="Accent" Color="#3574F0" />
                        <SolidColorBrush x:Key="Muted" Color="#6C707E" />
                    </ResourceDictionary>
                </ResourceDictionary.ThemeDictionaries>
                <Color x:Key="Brand">#E2A33F</Color>
                <Thickness x:Key="Gallery.Gap">8</Thickness>
            </ResourceDictionary>

            """;

        public const string AppXaml = """
            <Application xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="Gallery.App"
                         RequestedThemeVariant="Default">
                <Application.Resources>
                    <ResourceDictionary>
                        <ResourceDictionary.MergedDictionaries>
                            <ResourceInclude Source="avares://Gallery/Styles/Palette.axaml" />
                        </ResourceDictionary.MergedDictionaries>
                    </ResourceDictionary>
                </Application.Resources>

                <Application.Styles>
                    <FluentTheme />
                </Application.Styles>
            </Application>

            """;

        /// <summary>Styles that preview as the button their Design.PreviewWith holds, in the style's colour.</summary>
        public const string Buttons = """
            <Styles xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Design.PreviewWith>
                    <Border Padding="20">
                        <Button Classes="warm" Content="Warm" />
                    </Border>
                </Design.PreviewWith>
                <Style Selector="Button.warm">
                    <Setter Property="Background" Value="#E2A33F" />
                </Style>
            </Styles>

            """;

        /// <summary>The window's code-behind, using the field Avalonia's generator makes for the button named Go.</summary>
        public const string CodeBehind = """
            using Avalonia.Controls;

            namespace Gallery;

            public partial class MainWindow : Window
            {
                public MainWindow()
                {
                    InitializeComponent();
                    Go.Content = "Start";
                }
            }

            """;

        /// <summary>A window of the gallery's with its button's Click handled by OnGo, which <see cref="HandlingCodeBehind"/>
        /// declares.</summary>
        public static string Handled(string window) =>
            window.Replace("<Button Name=\"Go\" Content=\"Go\"", "<Button Name=\"Go\" Content=\"Go\" Click=\"OnGo\"", StringComparison.Ordinal);

        /// <summary>The window's code-behind with the handler of its button's Click below what <see cref="CodeBehind"/> has.</summary>
        public static readonly string HandlingCodeBehind = CodeBehind.Replace("        Go.Content = \"Start\";\n    }\n",
            "        Go.Content = \"Start\";\n    }\n\n    private void OnGo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)\n    {\n    }\n", StringComparison.Ordinal);

        public const string Window = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                    xmlns:vm="using:Gallery.ViewModels"
                    mc:Ignorable="d" d:DesignWidth="800" d:DesignHeight="450"
                    x:Class="Gallery.MainWindow"
                    x:DataType="vm:MainViewModel"
                    Title="Gallery">
                <StackPanel Margin="12" Spacing="8">
                    <TextBlock Name="Title" Text="{Binding Greeting}" />
                    <Button Name="Go" Content="Go" Background="{StaticResource Accent}" />
                    <TextBlock Text="{Binding #Go.Content}" />
                </StackPanel>
            </Window>

            """;
    }
}
