// This file contains the entire script. It prompts for the files, dynamically finds the column indexes based on
// your exact header names, creates the new assembly, resolves configurations, places and names the instances,
// applies the rotation and translation, and locks them in place.

using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class Program
{
    // Number of rows offered by the "First N rows" quick-test button in the
    // startup dialog. Automatically capped to the CSV's actual row count.
    const int MaxRowsForTest = 10;

    [STAThread] // Required for OpenFileDialog
    static void Main()
    {
        // 1. Prompt for input files
        Log("Showing CSV file dialog...");
        string csvPath = GetFile("Select Pattern CSV File", "CSV Files (*.csv)|*.csv");
        if (string.IsNullOrEmpty(csvPath)) return;
        Log($"CSV selected: {csvPath}");
        SetLogFolder(Path.GetDirectoryName(csvPath));

        Log("Showing subassembly file dialog... (if this seems stuck, check for a hidden dialog window)");
        string subAssyPath = GetFile("Select Subassembly or Part to Pattern", "SOLIDWORKS Assembly (*.sldasm)|*.sldasm|SOLIDWORKS Part (*.sldprt)|*.sldprt");
        if (string.IsNullOrEmpty(subAssyPath)) return;
        Log($"Subassembly selected: {subAssyPath}");

        string baseName = Path.GetFileNameWithoutExtension(subAssyPath);

        // 2. Read headers and dynamically map columns
        Log("Reading CSV file...");
        var lines = File.ReadAllLines(csvPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Log($"CSV read OK: {lines.Length} lines (including header).");
        var headers = lines[0].Split(',').Select(h => h.Trim()).ToList();

        // Ask how many rows to process (now that we know the CSV row count)
        int maxRows = AskRowCount(lines.Length - 1);
        if (maxRows < 0) return; // user closed the dialog

        int C(string name) => headers.IndexOf(name); // Helper to get column index
        
        if (C("type_id") < 0 || C("Rx_deg") < 0 || C("x_mm") < 0 || C("disk_id") < 0)
        {
            Console.WriteLine("CSV is missing required headers.");
            return;
        }

        // 3. Connect to SOLIDWORKS
        Log("Connecting to running SOLIDWORKS instance...");
        SldWorks swApp;
        try { swApp = (SldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
        catch { Console.WriteLine("SOLIDWORKS must be running first."); return; }
        Log("Connected to SOLIDWORKS.");

        // 4. Pre-load subassembly or part silently to read configurations
        int docType = subAssyPath.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) 
            ? (int)swDocumentTypes_e.swDocPART 
            : (int)swDocumentTypes_e.swDocASSEMBLY;

        int err = 0, warn = 0;
        Log($"Opening {subAssyPath} silently... (large assemblies can take a while; watch the SOLIDWORKS window for dialogs)");
        var subModel = swApp.OpenDoc6(subAssyPath, docType,
            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn);
        Log($"OpenDoc6 returned. Error code: {err}, Warning code: {warn}");
        
        if (subModel == null)
        {
            Console.WriteLine($"Failed to open the component. Error code: {err}");
            return;
        }

        string[] availableConfigs = (string[])subModel.GetConfigurationNames();

        // 5. Create the new parent assembly
        string template = swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
        Log($"Default assembly template preference: '{template}'");

        if (string.IsNullOrEmpty(template) || !File.Exists(template))
        {
            // Fallback: search the configured document template locations for an .asmdot file
            Log("Default template is empty or the file does not exist - searching template folders for an .asmdot file...");
            string templatesFolder = swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDocumentTemplates);
            Log($"Document templates folder preference: '{templatesFolder}'");

            template = null;
            if (!string.IsNullOrEmpty(templatesFolder))
            {
                foreach (string folder in templatesFolder.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder.Trim())) continue;
                    var hits = Directory.EnumerateFiles(folder.Trim(), "*.asmdot", SearchOption.AllDirectories).ToList();
                    if (hits.Count > 0) { template = hits[0]; break; }
                }
            }

            if (template == null)
            {
                Console.WriteLine("Error: could not find any .asmdot assembly template.");
                Console.WriteLine("Fix in SOLIDWORKS: Tools > Options > System Options > Default Templates, and set the assembly template.");
                return;
            }
            Log($"Using fallback template: {template}");
        }

        Log("Creating new parent assembly document...");
        var asmDoc = (ModelDoc2)swApp.NewDocument(template, 0, 0, 0);
        Log("New assembly document created.");
        if (asmDoc == null)
        {
            Console.WriteLine($"Error: Failed to create a new assembly document from template '{template}'.");
            return;
        }

        var asm = (AssemblyDoc)asmDoc;
        var math = (MathUtility)swApp.GetMathUtility();

        // 6. Process the CSV rows
        asmDoc.FeatureManager.EnableFeatureTree = false; // Pause tree updates for speed
        try
        {
            Log($"Processing {lines.Length - 1} CSV rows...");
            int lastRow = lines.Length - 1;
            if (maxRows > 0 && maxRows < lastRow)
            {
                lastRow = maxRows;
                Log($"LIMIT: patterning only the first {maxRows} data rows.");
            }

            for (int i = 1; i <= lastRow; i++)
            {
                var row = lines[i].Split(',');
                Log($"Row {i}/{lastRow}: type_id={row[C("type_id")]}");

                // Find matching configuration by checking if type_id is a substring
                string typeId = row[C("type_id")];
                string targetConfig = availableConfigs.FirstOrDefault(c => 
                    c.IndexOf(typeId, StringComparison.OrdinalIgnoreCase) >= 0) ?? availableConfigs[0];

                // Insert component
                var comp = asm.AddComponent5(subAssyPath, 
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, 
                    "", true, targetConfig, 0, 0, 0);

                if (comp == null) continue;

                // Rename the component instance in the feature tree.
                // NOTE: the name must NOT end in "-<digits>" - SOLIDWORKS parses a
                // trailing dash-number as the instance suffix and strips it from the
                // base name (e.g. "..._Pos-1" becomes "..._Pos"). Hence "Pos{xId}".
                string diskId = row[C("disk_id")];
                string rowId = row[C("row_id")];
                string xId = row[C("x_id")];
                string newName = $"{typeId}_Disk{diskId}_Row{rowId}_Pos{xId}";

                // Rename the component's feature in the tree directly (the component
                // feature's name IS the component name). Component2.Name2 is unreliable
                // on freshly inserted components, so we go through the feature.
                string actualName = comp.Name2;
                try
                {
                    Feature feat = (Feature)asmDoc.FirstFeature();
                    while (feat != null)
                    {
                        if (feat.Name == actualName) { feat.Name = newName; break; }
                        feat = (Feature)feat.GetNextFeature();
                    }
                    Log($"Row {i}: renamed '{actualName}' -> '{comp.Name2}'");
                }
                catch (Exception ex)
                {
                    Log($"Row {i}: rename failed: {ex.Message}");
                }

                // Parse and convert units (deg to rad, mm to m)
                double rx = D(row[C("Rx_deg")]) * Math.PI / 180.0;
                double ry = D(row[C("Ry_deg")]) * Math.PI / 180.0;
                double rz = D(row[C("Rz_deg")]) * Math.PI / 180.0;
                
                double tx = D(row[C("x_mm")]) / 1000.0;
                double ty = D(row[C("y_mm")]) / 1000.0;
                double tz = D(row[C("z_mm")]) / 1000.0;

                // Build 16-element transform array (Rotation first, then translation)
                double cx = Math.Cos(rx), sx = Math.Sin(rx);
                double cy = Math.Cos(ry), sy = Math.Sin(ry);
                double cz = Math.Cos(rz), sz = Math.Sin(rz);

                double[] matrix = new double[16] {
                    cz*cy, cz*sy*sx - sz*cx, cz*sy*cx + sz*sx,
                    sz*cy, sz*sy*sx + cz*cx, sz*sy*cx - cz*sx,
                    -sy,   cy*sx,            cy*cx,
                    tx, ty, tz,
                    1, 0, 0, 0
                };

                // Apply transform
                comp.Transform2 = (MathTransform)math.CreateTransform(matrix);

                // Select the component and fix it in place
                comp.Select4(false, null, false);
                asm.FixComponent();
            }
        }
        finally
        {
            // Always turn the feature tree back on, even if it crashes
            asmDoc.FeatureManager.EnableFeatureTree = true; 
        }

        Log("Rebuilding assembly...");
        asmDoc.EditRebuild3();

        // Set the view to shaded (no edges)
        Log("Setting view mode to shaded...");
        try
        {
            // Direct compile-time call: with EmbedInteropTypes the compiler embeds
            // any interop member referenced in source, which is more reliable than
            // dynamic/reflection dispatch on SOLIDWORKS COM objects (which produced
            // misleading DISP_E_BADINDEX errors and no-op property getters).
            asmDoc.ViewDisplayShaded();
            Log("View mode set to shaded.");
        }
        catch (Exception ex)
        {
            Log($"Could not set shaded view mode: {ex.Message}");
        }

        // Zoom to fit
        Log("Zoom to fit...");
        try
        {
            var activeDoc = (ModelDoc2)swApp.ActiveDoc;
            if (activeDoc != null)
            {
                activeDoc.ViewZoomtofit2();
                Log("Zoom to fit done.");
            }
        }
        catch (Exception ex)
        {
            Log($"Could not zoom to fit: {ex.Message}");
        }

        Log("Pattern generation complete!");
    }

    // Timestamped progress logging so hangs can be pinpointed to a specific call.
    // Also writes every line to pattern_run_log_<timestamp>.txt in the CSV's folder
    // (once known; earlier lines are buffered and flushed there afterwards).
    static readonly System.Collections.Generic.List<string> _logBuffer = new System.Collections.Generic.List<string>();
    static string _logFilePath = null;

    static void SetLogFolder(string folder)
    {
        _logFilePath = Path.Combine(folder,
            $"pattern_run_log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
        if (_logBuffer.Count > 0)
        {
            File.AppendAllLines(_logFilePath, _logBuffer);
            _logBuffer.Clear();
        }
    }

    static void Log(string msg)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}";
        Console.WriteLine(line);
        Console.Out.Flush();
        if (_logFilePath != null)
        {
            try { File.AppendAllText(_logFilePath, line + System.Environment.NewLine); }
            catch { /* never let logging break the run */ }
        }
        else
        {
            _logBuffer.Add(line);
        }
    }

    // Startup dialog: choose to process all CSV rows or just the first 10 (for quick tests).
    // Returns 0 for all rows, the test-row count for the test run, or -1 if the user
    // closed/cancelled. totalRows is the number of data rows in the CSV, so the test
    // button can adapt if the file has fewer than 10 rows.
    static int AskRowCount(int totalRows)
    {
        int testCount = Math.Min(MaxRowsForTest, totalRows);
        int choice = -1;

        using (var dlg = new Form
        {
            Text = "Pattern Tool",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new System.Drawing.Size(320, 90)
        })
        {
            var label = new Label
            {
                Text = "How many CSV rows should be patterned?",
                AutoSize = true,
                Location = new System.Drawing.Point(20, 15)
            };

            var allButton = new Button
            {
                Text = "All rows",
                DialogResult = DialogResult.Yes,
                Location = new System.Drawing.Point(20, 45),
                Size = new System.Drawing.Size(130, 30)
            };

            var testButton = new Button
            {
                Text = $"First {testCount} rows",
                DialogResult = DialogResult.No,
                Location = new System.Drawing.Point(165, 45),
                Size = new System.Drawing.Size(130, 30)
            };

            allButton.Click += (s, e) => { choice = 0; dlg.Close(); };
            testButton.Click += (s, e) => { choice = testCount; dlg.Close(); };

            dlg.Controls.Add(label);
            dlg.Controls.Add(allButton);
            dlg.Controls.Add(testButton);
            dlg.FormClosed += (s, e) => { if (choice < 0) choice = -1; };

            dlg.ShowDialog();
        }
        return choice;
    }

    // Helper to open file dialogs, forced to appear on top of all other windows
    // (a console app has no owner window, so dialogs can otherwise hide behind
    // SOLIDWORKS / VS Code).
    static string GetFile(string title, string filter)
    {
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0 })
        using (var ofd = new OpenFileDialog { Title = title, Filter = filter })
        {
            owner.Show();
            owner.Focus();
            var result = ofd.ShowDialog(owner);
            owner.Close();
            return result == DialogResult.OK ? ofd.FileName : null;
        }
    }

    // Helper to safely parse strings to doubles regardless of region settings
    static double D(string val) => double.Parse(val.Trim(), CultureInfo.InvariantCulture);
}
