# PDFTableExtract Demo
This is a demo application that was built as an experiment to see how accurately complex tables could be extracted from PDF documents. Complex tables are ones that may span multiple pages and be split either vertically or horizontally.

## Code
The code is written in C# using .NET 8.  The project uses third party Nuget packages.

## Requirements
The following Azure services are required for this demo:

- Azure Storage Account
- Azure Open AI Service with GPT-4O model deployed
- Azure Document Intelligence Service

## Configuration
This application utilizes an appsettings.json file to store the configuration settings for the Azure services. To use this demo you will need to create an appsettings.json file with the following settings:

- OpenAIModelDeployment - the name of the GPT-4O deployment in your Azure OpenAI Service.
- OpenAIEndpoint - URL for your Azure OpenAI service endpoint.
- OpenAIKey - Your Azure OpenAI Service API key
- OpenAIApiVersion - API version to use. I recommend 2023-03-15-preview
- DocIntelligenceEndpoint - URL for your Azure Document Intelligence service endpoint
- DocIntelligenceKey - API Key for your Azure Document Intelligence Service
- BlobSASConnectionString - The SAS URL for your Azure Storage Account. You will need to generate a SAS URL for your Storage account that provides access to add, delete, write and read blobs in the storage account.
- BlobContainer - The name of the container within the Azure storage account to use for temporary storage of processed PDF content. This content is used by Azure OpenAI and Azure Document Intelligence

## Approach
Here's a simplified explanation of how it works:
1. The application first asks for the name of the PDF file to process. If the file doesn't exist, it stops and alerts the user.
1.	It then reads configuration settings from a file called appsettings.json. These settings include details about the Azure services it uses.
1.	The application uploads the PDF file to Azure Blob Storage, a service that provides secure, scalable cloud storage.
1.	It uses Azure's Document Intelligence service to analyze the PDF. This service can identify where tables are located in the document.
1.	The application then identifies the pages that contain tables and extracts images of these pages from the PDF. If there are consecutive pages with tables, it stitches the images of these pages together.
1.	The stitched images are uploaded to Azure Blob Storage.
1.	The application then sends a request to Azure's OpenAI API. This request includes the URLs of the uploaded images and instructions for the AI to extract all tables from the images and return them as JSON.
1.	The AI processes the images and returns the extracted tables as JSON. If no tables are found, it returns a message saying "no table".
1.  The JSON is verified as being well formed. If it is not well formed, the user prompt is updated and the request is resent to Azure OpenAI. Only one additional attempt is made. If it fails the 2nd time, the table is skipped.
1.	The application then saves the JSON data to output files, one for each table. It also prints a message to the console for each file it creates.
1.	If there was an error calling the API, the application prints an error message to the console.

# Disclaimer
This is a demo application only and does not contain the necessary security or error handling required for a production application. Use this application for experimentation only. The application may contain bugs or produce incorrect output. The output it generated by AI and therefore may be inaccurate.