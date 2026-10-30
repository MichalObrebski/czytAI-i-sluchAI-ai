using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Tesseract; // Added Tesseract namespace

namespace czytAI_i_sluchAI_ai.Agent;

public sealed class ReadScreenTextTool : IAgentTool
{
    public string Name => "read_screen_text";

    public string Description => "Takes a screenshot of the primary monitor, extracts all visible text using OCR, and returns it.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new { }, // No parameters needed
        required = Array.Empty<string>()
    };

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        try
        {
            // SystemParameters must be accessed from the UI thread
            return Application.Current.Dispatcher.Invoke(() =>
            {
                int width = (int)SystemParameters.PrimaryScreenWidth;
                int height = (int)SystemParameters.PrimaryScreenHeight;

                // 1. Take the Screenshot
                using var bitmap = new Bitmap(width, height);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                }

                // 2. Convert to Byte Array (Tesseract requires PNG format)
                using var memoryStream = new MemoryStream();
                bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                byte[] imageBytes = memoryStream.ToArray();

                // 3. Initialize Tesseract and extract text
                // It looks for the "tessdata" folder in your build directory
                // 1. Get the absolute path to where the app is running
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string tessDataPath = "C:\\Users\\topiw\\czytAI-i-sluchAI-ai\\src\\czytAI-i-sluchAI-ai\\tessdata";
                string trainedDataFilePath = System.IO.Path.Combine(tessDataPath, "eng.traineddata");

                // 2. Verify the file actually exists before crashing!
                if (!System.IO.File.Exists(trainedDataFilePath))
                {
                    return Task.FromResult($"Configuration Error: The OCR language file was not found at {trainedDataFilePath}. Please ensure 'eng.traineddata' is set to 'Copy to Output Directory'.");
                }

                // 3. Initialize Tesseract using the absolute path
                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                using var img = Pix.LoadFromMemory(imageBytes);
                using var page = engine.Process(img);

                string extractedText = page.GetText();

                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    return Task.FromResult("Screenshot taken, but no readable text was found on the screen.");
                }

                // 4. Token Limit Safety Check
                // A full screen of text could easily exceed a local AI's context limit. 
                // We truncate it to 2500 characters to prevent the model from crashing.
                if (extractedText.Length > 2500)
                {
                    extractedText = extractedText.Substring(0, 2500) + "\n...[Text truncated for length]";
                }

                return Task.FromResult($"Here is the text currently visible on the user's screen:\n{extractedText}");
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Failed to extract text from screen: {ex.Message}");
        }
    }
}