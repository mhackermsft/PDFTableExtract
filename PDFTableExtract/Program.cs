// DEMO CODE ONLY
// REQUIRES ANONYMOUS ACCESS ENABLED ON BLOB STORAGE SO AZURE OPENAI API CAN ACCESS IMAGES


using SkiaSharp;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using Azure.Storage.Blobs;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Configuration;
using System.Drawing;
string pdfName = string.Empty;

// Read configuration settings from appsetting.json
var config = new ConfigurationBuilder()
	.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
	.Build();

//Get command line arguments
Console.ResetColor();
var arguments = Environment.GetCommandLineArgs();

//See if we have a command line argument for the PDF file name.  If we do, set the pdfName variable to the argument, otherwise prompt the user for the PDF file name.
if (arguments.Length > 1)
{
	pdfName = arguments[1];
}
else
{
	Console.WriteLine("Enter the full path to the PDF file to extract tables from:");
	pdfName = Console.ReadLine()?? string.Empty;
}

Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine($"Processing file {pdfName}.");

//verify that the PDF exists
if (!File.Exists(pdfName))
{
	Console.ForegroundColor = ConsoleColor.Red;
	Console.WriteLine($"The file {pdfName} does not exist.");
	Console.ResetColor();
	return;
}


// Set up configuration settings
string modelDeployment = config["OpenAIModelDeployment"]??string.Empty;
string oaiendpoint = config["OpenAIEndpoint"] ?? string.Empty;
string apikey = config["OpenAIKey"] ?? string.Empty;
string apiVersion = config["OpenAIApiVersion"] ?? string.Empty;
string docAIEndpoint = config["DocIntelligenceEndpoint"] ?? string.Empty;
string docAIKey = config["DocIntelligenceKey"] ?? string.Empty;
string blobSASConnectionString = config["BlobSASConnectionString"] ?? string.Empty;
string containerName = config["BlobContainerName"] ?? string.Empty;
string endpoint = $"{oaiendpoint}openai/deployments/{modelDeployment}/chat/completions?api-version={apiVersion}";


//Ensure that a folder called Output exists in the current running folder
if (!Directory.Exists("output"))
{
	Directory.CreateDirectory("output");
}

//See if a folder for the document exists, if not create it
string doc = Path.GetFileNameWithoutExtension(pdfName).Replace(" ", "_");
string outputPath = $"output\\{doc}";
if (!Directory.Exists(outputPath))
{
	Directory.CreateDirectory(outputPath);
}
else
{
	//Delete all files in that direcotry
	DirectoryInfo di = new DirectoryInfo(outputPath);
	foreach (FileInfo file in di.GetFiles())
	{
		file.Delete();
	}
}

Console.WriteLine($"Output directory set to: {outputPath}");

Console.WriteLine("Uploading PDF to blob storage and detecting tables with Document Intelligence.");
//upload original PDF to blob storage using Azure SDK with SAS token URL
//create blob client using blob SAS URL stored in blobSASConnectionString
var blobServiceClient = new BlobServiceClient(new Uri(blobSASConnectionString));
var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);

var blobClient = blobContainerClient.GetBlobClient(Path.GetFileName(pdfName).Replace(" ", "_"));
blobClient.Upload(pdfName, overwrite:true);

//Take the PDF and run it through document intelligence to figure out what pages have tables. Use the Azure Document Intelligence SDK
var client = new DocumentIntelligenceClient(new Uri(docAIEndpoint), new AzureKeyCredential(docAIKey));
var content = new AnalyzeDocumentContent() { UrlSource = new Uri(blobClient.Uri.GetLeftPart(UriPartial.Path)) };
Operation<AnalyzeResult> operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", content);
AnalyzeResult result = operation.Value;

//Get page numbers that have tables
Console.WriteLine("Identify pages that have tables.");
List<int> pageNumbers = new List<int>();
for (int x=0; x<result.Tables.Count; x++)
{
	var table = result.Tables[x];
	foreach (var region in table.BoundingRegions)
	{
		pageNumbers.Add(region.PageNumber);
	}
}

//remove duplicates
pageNumbers = pageNumbers.Distinct().ToList();

//Make sure page numbers are sorted in ascending order
pageNumbers.Sort();


//Use the page numbers to extract the images from the PDF.  If the pages are consecutive, stitch them together to reduce the number of images to 20 or less.
var pdf = await File.ReadAllBytesAsync(pdfName);
var pageImages = PDFtoImage.Conversion.ToImages(pdf).ToList();

//Find the pageNumbers that are consecutive and group them together
Console.WriteLine("Group consecutive pages together.");
var consecutivePageNumbers = new List<List<int>>();
var currentConsecutivePageNumbers = new List<int>();
for (int i = 0; i < pageNumbers.Count; i++)
{
	if (i == 0)
	{
		currentConsecutivePageNumbers.Add(pageNumbers[i]);
	}
	else
	{
		if (pageNumbers[i] == pageNumbers[i - 1] + 1)
		{
			currentConsecutivePageNumbers.Add(pageNumbers[i]);
		}
		else
		{
			consecutivePageNumbers.Add(currentConsecutivePageNumbers);
			currentConsecutivePageNumbers = new List<int> { pageNumbers[i] };
		}
	}
}
consecutivePageNumbers.Add(currentConsecutivePageNumbers);

//for each consecutive page number group, get the pageImage
int consecutivePageCount = 0;
foreach (var consecutivePageNumberGroup in consecutivePageNumbers)
{
	Console.WriteLine("Extracting images from consecutive pages group.");
	var consecutivePageImages = new List<SKBitmap>();
	foreach (var pageNumber in consecutivePageNumberGroup)
	{
		consecutivePageImages.Add(pageImages[pageNumber - 1]);
	}

	int totalPageCount = consecutivePageImages.Count;

	//if we have more than 20 pages, we need to stitch the images together
	int maxSize = (int)Math.Ceiling(totalPageCount / 20.0);
	var pageImageGroups = new List<List<SKBitmap>>();
	for (int i = 0; i < totalPageCount; i += maxSize)
	{
		var pageImageGroup = consecutivePageImages.Skip(i).Take(maxSize).ToList();
		pageImageGroups.Add(pageImageGroup);
	}

	var userPromptParts = new List<JsonNode>{
		new JsonObject
		{
			{ "type", "text" },
			{ "text", $"Extract all tables or tabular data from the images to valid JSON. Some tables span multiple images. Tables in images could be split horizontally or vertically across images. Each table should be a separate JSON document. Separate JSON documents by two line breaks. Do not convert table of contents into JSON document. If no JSON will be generated, respond only with 'no table'. Do not use code blocks." }
		}
	};

	var pdfImageFiles = new List<string>();

	var count = 0;
	foreach (var pageImageGroup in pageImageGroups)
	{
		var pdfImageName = $"{pdfName}.Page_{consecutivePageCount}.Part_{count}.jpg";

		int totalHeight = pageImageGroup.Sum(image => image.Height);
		int width = pageImageGroup.Max(image => image.Width);
		var stitchedImage = new SKBitmap(width, totalHeight);
		var canvas = new SKCanvas(stitchedImage);
		int currentHeight = 0;
		foreach (var pageImage in pageImageGroup)
		{
			canvas.DrawBitmap(pageImage, 0, currentHeight);
			currentHeight += pageImage.Height;
		}
		using (var stitchedFileStream = new FileStream(pdfImageName, FileMode.Create, FileAccess.Write))
		{
			stitchedImage.Encode(stitchedFileStream, SKEncodedImageFormat.Jpeg, 100);
		}
		pdfImageFiles.Add(pdfImageName);
		count++;

		//upload image to blob storage using Azure SDK with SAS token URL

		string blobName = Path.GetFileName(pdfImageName).Replace(" ", "_");
		blobClient = blobContainerClient.GetBlobClient(blobName);

		//check if blob file exists and if so delete it
		if (await blobClient.ExistsAsync())
		{
			await blobClient.DeleteAsync();
		}

		Console.ForegroundColor = ConsoleColor.Blue;
		Console.WriteLine($"Uploading image {pdfImageName} to blob storage.");
		Console.ForegroundColor = ConsoleColor.Yellow;
		await blobClient.UploadAsync(pdfImageName);

		//get url of the uploaded image
		var blobUri = blobClient.Uri;

		userPromptParts.Add(new JsonObject
	{
		{ "type", "image_url" },
		{ "image_url", new JsonObject { { "url", blobUri.GetLeftPart(UriPartial.Path) } } }
	});

		File.Delete(pdfImageName);

	}

	JsonObject jsonPayload = new JsonObject
	{
		{
			"messages", new JsonArray
			{
				new JsonObject
				{
					{ "role", "system" },
					{ "content", "You are an AI assistant that extracts all tables from images and returns them as individual valid JSON blocks. Do not return as a code block." }
				},
				new JsonObject
				{
					{ "role", "user" },
					{ "content", new JsonArray(userPromptParts.ToArray())}
				}
			}
		},
		{ "model", modelDeployment },
		{ "max_tokens", 4096 },
		{ "temperature", 0.1 },
		{ "top_p", 0.1 },
	};

	string payload = JsonSerializer.Serialize(jsonPayload, new JsonSerializerOptions
	{
		WriteIndented = true,
	});


	using (var httpClient = new HttpClient())
	{
		httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
		httpClient.BaseAddress = new Uri(endpoint);
		httpClient.DefaultRequestHeaders.Add("api-key", apikey);
		httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

		var stringContent = new StringContent(payload, Encoding.UTF8, "application/json");

		Console.ForegroundColor= ConsoleColor.Green;
		Console.WriteLine("Calling Azure OpenAI API to extract tables from images.");
		Console.ForegroundColor = ConsoleColor.Yellow;
		var response = await httpClient.PostAsync(endpoint, stringContent);

		if (response.IsSuccessStatusCode)
		{
			using (var responseStream = await response.Content.ReadAsStreamAsync())
			{
				// Parse the JSON response using JsonDocument
				using (var jsonDoc = await JsonDocument.ParseAsync(responseStream))
				{
					// Access the message content dynamically
					JsonElement jsonElement = jsonDoc.RootElement;
					string? messageContent = jsonElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

					if (messageContent == "no table")
					{
						Console.WriteLine("No tables found in the images.");
						continue;
					}
					else
					{
						//split the message content on two line breaks
						if (!string.IsNullOrEmpty(messageContent))
						{
							string[] jsonDocuments = messageContent.Split(new string[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

							//write out the content for each JSON document
							for (int i = 0; i < jsonDocuments.Length; i++)
							{
								//validate json
								try
								{
									JsonDocument.Parse(jsonDocuments[i]);
								}
								catch(Exception ex)
								{
									Console.ForegroundColor = ConsoleColor.Red;
									Console.WriteLine($"JSON document {i} is not valid. {ex.Message}");
									Console.ForegroundColor= ConsoleColor.Yellow;
									continue;
								}

								string output = $"{outputPath}\\output_page_{consecutivePageCount}_table_{i}.json";
								File.WriteAllText(output, jsonDocuments[i]);
								Console.ForegroundColor = ConsoleColor.Green;
								Console.WriteLine($"{output} has been created with the content from the response from the OpenAI API.");
								Console.ForegroundColor = ConsoleColor.Yellow;
								//TODO load into Cosmos DB

							}
						}
						else
						{
							Console.WriteLine("No tables found in the images.");
						}
					}
				}
			}
		}
		else
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine($"Error calling API: {response}");
			string results = await response.Content.ReadAsStringAsync();
			Console.WriteLine($"Reason: {results}");
			Console.ResetColor();
		}
	}

	consecutivePageCount++;
}
