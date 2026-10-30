using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using GdPicture14;

namespace dotnet_twain
{
    internal class Program
    {
        // Default folder of the OCR resources installed by the GdPicture.NET 14 setup on Windows.
        private const string DefaultOcrResourcesPath = @"C:\GdPicture.NET 14\Redist\OCR";

        static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configure CORS to allow requests from the web client
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });

            var app = builder.Build();

            app.UseCors();

            // GdPicture license key from appsettings.json or the GDPICTURE_LICENSE_KEY environment variable.
            // An empty key starts GdPicture in evaluation mode. RegisterKEY must still be called.
            var licenseKey = app.Configuration["GDPICTURE_LICENSE_KEY"] ?? string.Empty;
            new GdPicture14.LicenseManager().RegisterKEY(licenseKey.Trim());

            // Folder that holds the OCR resources (eng.traineddata).
            var ocrResourcesPath = app.Configuration["GDPICTURE_OCR_RESOURCES_PATH"];
            if (string.IsNullOrWhiteSpace(ocrResourcesPath))
            {
                ocrResourcesPath = DefaultOcrResourcesPath;
            }

            // API endpoint to trigger TWAIN scan with Server-Sent Events for progress
            app.MapPost("/api/scan", async (HttpContext context) =>
            {
                context.Response.Headers.Append("Content-Type", "text/event-stream");
                context.Response.Headers.Append("Cache-Control", "no-cache");
                context.Response.Headers.Append("Connection", "keep-alive");

                // Each scan is written to its own temporary file, deleted once it is sent.
                string pdfPath = Path.Combine(Path.GetTempPath(), $"twain-scan-{Guid.NewGuid():N}.pdf");

                try
                {
                    Console.WriteLine("Starting TWAIN scan...");

                    // Progress callback to send events to client
                    async Task SendProgress(string message, string stage, int current = 0, int total = 0)
                    {
                        var data = System.Text.Json.JsonSerializer.Serialize(new { message, stage, current, total });
                        await context.Response.WriteAsync($"data: {data}\n\n");
                        await context.Response.Body.FlushAsync();
                    }

                    await SendProgress("Initializing scanner...", "init");
                    var result = await Task.Run(() => TWAIN(pdfPath, ocrResourcesPath, SendProgress));

                    // Read the PDF file and convert to base64
                    if (System.IO.File.Exists(pdfPath))
                    {
                        await SendProgress("Reading PDF file...", "reading");
                        byte[] pdfBytes = System.IO.File.ReadAllBytes(pdfPath);
                        string base64Pdf = Convert.ToBase64String(pdfBytes);

                        var finalData = System.Text.Json.JsonSerializer.Serialize(new {
                            success = true,
                            message = "Scan completed",
                            base64 = base64Pdf,
                            fileName = "scanned-document.pdf",
                            stage = "complete"
                        });
                        await context.Response.WriteAsync($"data: {finalData}\n\n");
                        await context.Response.Body.FlushAsync();
                    }
                    else
                    {
                        var errorData = System.Text.Json.JsonSerializer.Serialize(new {
                            success = false,
                            message = "PDF file not found",
                            stage = "error"
                        });
                        await context.Response.WriteAsync($"data: {errorData}\n\n");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during scan: {ex.Message}");
                    var errorData = System.Text.Json.JsonSerializer.Serialize(new {
                        success = false,
                        message = ex.Message,
                        stage = "error"
                    });
                    await context.Response.WriteAsync($"data: {errorData}\n\n");
                }
                finally
                {
                    if (System.IO.File.Exists(pdfPath))
                    {
                        System.IO.File.Delete(pdfPath);
                    }
                }
            });

            app.MapGet("/", () => "TWAIN API is running. Use POST /api/scan to trigger a scan.");

            Console.WriteLine(" ---- Nutrient .NET SDK - TWAIN API ----");
            Console.WriteLine("API listening on http://localhost:5000");

            app.Run("http://localhost:5000");
        }

        [STAThread]
        public static string TWAIN(string outputPath, string ocrResourcesPath, Func<string, string, int, int, Task>? progressCallback = null)
        {
            int ImageID = 0;
            bool bContinue = false;
            string message = "Done.\n";

            bool ocrEnabled = Directory.Exists(ocrResourcesPath);
            if (!ocrEnabled)
            {
                Console.WriteLine($"OCR resources folder not found: {ocrResourcesPath}. Pages are saved without OCR.");
            }

            GdPictureImaging oGdPictureImaging = new GdPictureImaging();
            GdPicturePDF oGdPicturePDF = new GdPicturePDF();

            // Use IntPtr.Zero as the HANDLE in static context
            var handle = IntPtr.Zero;
            try
            {
                if (oGdPictureImaging.TwainSelectSource(handle) &&
                    oGdPictureImaging.TwainOpenDefaultSource(handle))
                {
                    oGdPictureImaging.TwainOpenDefaultSource(handle);

                    oGdPictureImaging.TwainSetAutoFeed(true);
                    oGdPictureImaging.TwainSetAutoScan(true);
                    oGdPictureImaging.TwainSetResolution(200);
                    oGdPictureImaging.TwainSetPixelType(TwainPixelType.TWPT_RGB);
                    oGdPictureImaging.TwainSetBitDepth(1);

                    oGdPicturePDF.NewPDF(PdfConformance.PDF2_0);

                    // Safety: timeouts to avoid infinite blocking if driver hangs
                    var lastProgress = Stopwatch.StartNew();
                    const int stallTimeoutSeconds = 120; // adjust if needed

                    // Guard against repeated TWAIN error loops
                    int consecutiveTwainErrors = 0;
                    const int maxConsecutiveErrors = 6;
                    int pageCount = 0;

                    do
                    {
                        Console.WriteLine($"Starting acquisition. Twain state: {oGdPictureImaging.TwainGetState()}");
                        ImageID = oGdPictureImaging.TwainAcquireToGdPictureImage(handle);

                        // Log GdPicture imaging status and TWAIN last codes for debugging
                        var stat = oGdPictureImaging.GetStat();
                        Console.WriteLine($"Acquire returned ImageID={ImageID}; GetStat={stat}; TwainLastResult={oGdPictureImaging.TwainGetLastResultCode()}; TwainCond={oGdPictureImaging.TwainGetLastConditionCode()}");

                        if (ImageID > 0 && stat == GdPictureStatus.OK)
                        {
                            consecutiveTwainErrors = 0;
                            pageCount++;

                            progressCallback?.Invoke($"Page scanned {pageCount}", "scanning", pageCount, 0).Wait();

                            // Only add a new page if this is not the first page
                            if (pageCount > 1)
                            {
                                oGdPicturePDF.NewPage(PdfPageSizes.PdfPageSizeLetter);
                            }

                            oGdPicturePDF.SelectPage(oGdPicturePDF.GetPageCount());
                            oGdPicturePDF.AddImageFromGdPictureImage(ImageID, true, true);
                            Console.WriteLine($"Added image to PDF, PDF stat={oGdPicturePDF.GetStat()}");

                            if (ocrEnabled && oGdPicturePDF.GetStat() == GdPictureStatus.OK)
                            {
                                progressCallback?.Invoke($"OCR processing page {pageCount}", "ocr", pageCount, 0).Wait();

                                var ocrStat = oGdPicturePDF.OcrPage("eng", ocrResourcesPath, "", 150);
                                Console.WriteLine($"OcrPage returned stat={ocrStat}");
                            }

                            message += "Page #" + oGdPicturePDF.GetCurrentPage().ToString() + " - status: " + oGdPicturePDF.GetStat().ToString() + "\n";

                            oGdPictureImaging.ReleaseGdPictureImage(ImageID);

                            // progress made -> reset timeout
                            lastProgress.Restart();
                        }
                        else
                        {
                            // No valid image: driver likely returned a TWAIN error or is waiting for UI/profile
                            Console.WriteLine("No image acquired or error during acquisition.");

                            // Inspect TWAIN state for additional hints
                            var twainState = oGdPictureImaging.TwainGetState();
                            if (twainState == TwainStatus.TWAIN_SM_OPEN)
                            {
                                Console.WriteLine("TWAIN_SM_OPEN: source manager open but source not enabled. The driver may be showing a modal dialog (profile selection) or requires a saved profile.");
                            }

                            consecutiveTwainErrors++;

                            // Exponential/backoff delay to avoid hammering the driver
                            int backoffMs = Math.Min(5000, 250 * consecutiveTwainErrors);
                            Thread.Sleep(backoffMs);

                            // If driver repeatedly fails, abort to avoid infinite loop
                            if (consecutiveTwainErrors >= maxConsecutiveErrors)
                            {
                                Console.WriteLine($"Aborting acquisition after {consecutiveTwainErrors} consecutive errors.");
                                bContinue = false;
                                break;
                            }
                        }

                        // Continue while the TWAIN source indicates enabled (pages may be available)
                        bContinue = oGdPictureImaging.TwainGetState() <= TwainStatus.TWAIN_SOURCE_ENABLED;

                        // Small sleep to avoid tight loop and give the driver time
                        Thread.Sleep(100);

                        // If no progress for a certain time, break to avoid hanging forever
                        if (lastProgress.Elapsed.TotalSeconds > stallTimeoutSeconds)
                        {
                            Console.WriteLine($"No progress for {stallTimeoutSeconds} seconds — aborting acquisition loop to avoid a hang.");
                            bContinue = false;
                        }

                    } while (bContinue);

                    // Try to save the PDF and log result
                    progressCallback?.Invoke($"Saving document ({pageCount} pages)", "saving", 0, 0).Wait();
                    var saveStat = oGdPicturePDF.SaveToFile(outputPath, true);
                    Console.WriteLine($"SaveToFile returned stat={saveStat}; PDF stat={oGdPicturePDF.GetStat()}");
                    message += "Saving - status: " + oGdPicturePDF.GetStat().ToString();

                    oGdPictureImaging.TwainCloseSource();

                    Console.WriteLine(message);
                }
                else
                {
                    Console.WriteLine("Can't open the default source.\nresult code: " + oGdPictureImaging.TwainGetLastResultCode() +
                                           "\ncondition code: " + oGdPictureImaging.TwainGetLastConditionCode());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex}");
            }
            finally
            {
                oGdPictureImaging.Dispose();
                oGdPicturePDF.Dispose();
            }

            return message;
        }
    }
}
