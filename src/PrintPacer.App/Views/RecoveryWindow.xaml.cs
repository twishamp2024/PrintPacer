using System.Windows;
using PrintPacer.Core.Engine;

namespace PrintPacer.App.Views;

/// <summary>
/// Shown once at startup when unfinished jobs were found.
///
/// Its job is to be honest about the difference between "this carried on fine" and "nobody
/// can be sure what came out of the printer". The second case is the one the user needs to
/// act on, so it is stated plainly rather than buried.
/// </summary>
public partial class RecoveryWindow : Window
{
    public RecoveryWindow(IReadOnlyList<RecoveryReport> reports)
    {
        InitializeComponent();

        Reports.ItemsSource = reports;

        var needingDecision = reports.Count(r => r.NeedsUserDecision);

        if (needingDecision == 0)
        {
            Headline.Text = reports.Count == 1
                ? "Picking up where it left off"
                : $"Picking up {reports.Count} unfinished jobs";

            Subhead.Text = "Nothing was interrupted mid-print. Printing will continue from the next " +
                           "batch that has not been sent.";

            Advice.Text = "No pages will be printed twice.";
        }
        else
        {
            Headline.Text = needingDecision == 1
                ? "One batch was interrupted"
                : $"{needingDecision} batches were interrupted";

            Subhead.Text = "PrintPacer stopped while these pages were in the printer's queue, so it cannot " +
                           "be certain whether they came out.";

            Advice.Text = "Check the output tray, then tell PrintPacer what you found on the job screen. " +
                          "Nothing will be sent again until you do.";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
