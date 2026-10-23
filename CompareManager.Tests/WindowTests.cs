using Xunit;

namespace CompareManager.Tests
{
    public class WindowTests
    {
        /// <summary>
        /// Building the main window runs every control's layout once. A source card used to
        /// read its caption during the first of those, before the caption existed, and the app
        /// failed on start with "Object reference not set" — which no logic test could see.
        /// </summary>
        [Fact]
        public void The_main_window_can_be_built()
        {
            using var form = new MainForm();
            Assert.Equal("Compare Manager", form.Text);
        }
    }
}
