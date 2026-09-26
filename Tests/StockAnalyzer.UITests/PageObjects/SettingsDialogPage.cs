using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using StockAnalyzer.UITests.Infrastructure;
using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace StockAnalyzer.UITests.PageObjects
{
    public class SettingsDialogPage
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        private readonly AutomationElement _dialog;
        private readonly UITestBase _testBase;

        public SettingsDialogPage(AutomationElement dialog, UITestBase testBase)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _testBase = testBase ?? throw new ArgumentNullException(nameof(testBase));
        }

        public void AddIndicator(string indicatorName, string? periodStr = null)
        {
            // The current manager adds indicators from the Library catalog, not a popup menu.
            var libraryTab = _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Tab_Library"));
            if (libraryTab != null && !libraryTab.IsOffscreen)
                libraryTab.Click();

            var searchBox = _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Library_SearchBox"))?.AsTextBox(),
                errorMessage: "Indicator Library search box not found");
            searchBox.Text = indicatorName;

            var catalog = _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Library_CatalogList"))?.AsListBox(),
                errorMessage: "Indicator Library catalog not found");
            var catalogItem = _testBase.WaitForElement(() =>
                catalog.Items.FirstOrDefault(item =>
                    item.FindFirstDescendant(cf => cf.ByName(indicatorName)) != null),
                errorMessage: $"Indicator '{indicatorName}' not found in Library catalog");
            catalogItem.Click();

            if (periodStr != null)
            {
                var expectedPeriod = int.Parse(periodStr, CultureInfo.InvariantCulture);
                var periodLabel = _testBase.WaitForElement(() =>
                    _dialog.FindFirstDescendant(cf => cf.ByName("Period")),
                    errorMessage: $"Period label for '{indicatorName}' not found");
                var labelBounds = periodLabel.BoundingRectangle;
                var periodInput = _testBase.WaitForElement(() =>
                    _dialog.FindAllDescendants(cf =>
                            cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.Spinner)))
                        .Where(element =>
                        {
                            var bounds = element.BoundingRectangle;
                            return bounds.Left >= labelBounds.Right &&
                                   bounds.Top < labelBounds.Bottom &&
                                   bounds.Bottom > labelBounds.Top;
                        })
                        .OrderBy(element => element.BoundingRectangle.Left)
                        .FirstOrDefault(),
                    errorMessage: $"Period editor beside the label for '{indicatorName}' not found");

                if (periodInput.ControlType == ControlType.Spinner)
                    periodInput.AsSpinner().Value = expectedPeriod;
                else
                    periodInput.AsTextBox().Text = periodStr;
            }

            var addButton = _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Library_AddActiveButton"))?.AsButton(),
                errorMessage: "Library Activate button not found");
            addButton.Click();
        }

        public void VerifyActiveIndicator(string displayName)
        {
            var activeTab = _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Tab_Active_Lib")),
                errorMessage: "Active tab not found in Library mode");
            activeTab.Click();

            _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Settings_ActiveIndicatorList"))?.AsListBox(),
                errorMessage: "Active indicator list not found");
            var selectedTitle = _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Settings_SelectedIndicatorTitle")),
                errorMessage: "Selected indicator title not found");
            _testBase.WaitUntil(() => selectedTitle.Name == displayName,
                errorMessage: $"Selected indicator was '{selectedTitle.Name}', expected '{displayName}'");
        }

        public void Close()
        {
            var dialogHandle = _dialog.Properties.NativeWindowHandle.Value;
            if (dialogHandle == IntPtr.Zero)
                throw new InvalidOperationException("Indicator Manager has no native window handle");

            var okButton = _testBase.WaitForElement(() =>
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Settings_OkButton"))?.AsButton(),
                errorMessage: "Indicator Manager OK button not found");
            okButton.Click();

            _testBase.WaitUntil(() => !IsWindow(dialogHandle),
                errorMessage: "Indicator Manager failed to close");
        }
    }
}
