// The project enables both WPF and Windows Forms: WPF for the interface, Windows Forms
// solely for its NotifyIcon, which is still the only supported way to put an icon in the
// notification area from a desktop application.
//
// Both frameworks contribute implicit usings, and several type names exist in both. These
// aliases settle every collision in favour of WPF, so the rest of the application can be
// written without repeating fully-qualified names. The one place that genuinely needs the
// Windows Forms types (TrayNotifier) names them explicitly.

global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Colors = System.Windows.Media.Colors;
global using DataFormats = System.Windows.DataFormats;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DragEventArgs = System.Windows.DragEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using TextBox = System.Windows.Controls.TextBox;
global using Binding = System.Windows.Data.Binding;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
