// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.WebService;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Primitives;
using System.Net.Http;
using System.Xml;

namespace Microsoft.KernelMemory.Service;

internal static class WebAPIEndpoints
{
    private static readonly DateTimeOffset s_start = DateTimeOffset.UtcNow;

    public static void ConfigureMinimalAPI(this WebApplication app, KernelMemoryConfig config)
    {
        if (!config.Service.RunWebService) { return; }

        app.UseSwagger(config);

        var authFilter = new HttpAuthEndpointFilter(config.ServiceAuthorization);

        app.UseGetStatusEndpoint(authFilter);
        app.UsePostUploadEndpoint(authFilter);
        app.UseAddUrlEndpoint(authFilter);
        app.UseAddSitemapEndpoint(authFilter);
        app.UseGetIndexesEndpoint(authFilter);
        app.UseDeleteIndexesEndpoint(authFilter);
        app.UseDeleteDocumentsEndpoint(authFilter);
        app.UseAskEndpoint(authFilter);
        app.UseSearchEndpoint(authFilter);
        app.UseUploadStatusEndpoint(authFilter);
    }

    public static void UseGetStatusEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // Simple ping endpoint
        var route = app.MapGet("/", () => Results.Ok("Ingestion service is running. " +
                                                     "Uptime: " + (DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                                                   - s_start.ToUnixTimeSeconds()) + " secs " +
                                                     $"- Environment: {Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}"))
            .Produces<string>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UsePostUploadEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // File upload endpoint
        var route = app.MapPost(Constants.HttpUploadEndpoint, async Task<IResult> (
                HttpRequest request,
                IKernelMemory service,
                ILogger<WebAPIEndpoint> log,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New upload HTTP request, content length {0}", request.ContentLength);

                // Note: .NET doesn't yet support binding multipart forms including data and files
                (HttpDocumentUploadRequest input, bool isValid, string errMsg)
                    = await HttpDocumentUploadRequest.BindHttpRequestAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                log.LogTrace("Index '{0}'", input.Index);

                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }

                try
                {
                    // UploadRequest => Document
                    var documentId = await service.ImportDocumentAsync(input.ToDocumentUploadRequest(), cancellationToken)
                        .ConfigureAwait(false);

                    log.LogTrace("Doc Id '{1}'", documentId);

                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal);
                    return Results.Accepted(url, new UploadAccepted
                    {
                        DocumentId = documentId,
                        Index = input.Index,
                        Message = "Document upload completed, ingestion pipeline started"
                    });
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "Document upload failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseAddUrlEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // File upload endpoint
        var route = app.MapPost("/addurl", async Task<IResult> (
                HttpRequest request,
                IKernelMemory service,
                ILogger<WebAPIEndpoint> log,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New add url HTTP request, content length {0}", request.ContentLength);

                // Note: .NET doesn't yet support binding multipart forms including data and files
                (HttpAddUrlRequest input, bool isValid, string errMsg)
                    = await HttpAddUrlRequest.BindHttpRequestAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                log.LogTrace("Index '{0}'", input.Index);

                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }

                try
                {
                    //await s_memory.ImportWebPageAsync("https://raw.githubusercontent.com/microsoft/kernel-memory/main/README.md", documentId: "webPage1");
                    // UploadRequest => Document
                    var documentId = await service.ImportWebPageAsync(
                        url: input.Url.ToString(),
                        documentId: input.DocumentId,
                        tags: input.Tags,
                        index: input.Index,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                    log.LogTrace("Doc Id '{1}'", documentId);

                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal);
                    return Results.Accepted(url, new UploadAccepted
                    {
                        DocumentId = documentId,
                        Index = input.Index,
                        Message = "Document upload completed, ingestion pipeline started"
                    });
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "Document upload failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseAddSitemapEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // File upload endpoint
        var route = app.MapPost("/addsitemap", async Task<IResult> (
                HttpRequest request,
                IKernelMemory service,
                ILogger<WebAPIEndpoint> log,
                //IHttpClientFactory clientFactory,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New add sitemap HTTP request, content length {0}", request.ContentLength);

                // Note: .NET doesn't yet support binding multipart forms including data and files
                (HttpAddUrlRequest input, bool isValid, string errMsg)
                    = await HttpAddUrlRequest.BindHttpRequestAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                log.LogTrace("Index '{0}'", input.Index);

                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }

                try
                {
                    // get http client and use it to retrieve sitemap xml from "url"
                    /*var client = clientFactory.CreateClient();
                    var response = await client.GetAsync(input.Url, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var sitemap = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);*/
                    var docIds = new List<string>();

                    // Load the sitemap XML from the URL
                    var doc = new XmlDocument();
                    doc.Load(input.Url.ToString());

                    // Namespace manager for handling namespaces in the XML
                    var manager = new XmlNamespaceManager(doc.NameTable);
                    manager.AddNamespace("ns", "http://www.sitemaps.org/schemas/sitemap/0.9");

                    // Select all <url> nodes and iterate over them
                    var nodes = doc.SelectNodes("//ns:url/ns:loc", manager);
                    foreach (XmlNode node in nodes)
                    {
                        var documentId = await service.ImportWebPageAsync(
                        url: node.InnerText,
                        tags: input.Tags,
                        index: input.Index,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                        docIds.Add(documentId);

                        // wait for 1 second to avoid rate limiting
                        await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                    }
                    log.LogTrace("Doc Ids '{1}'", string.Join(',', docIds));

                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, string.Join(',', docIds), StringComparison.Ordinal);
                    return Results.Accepted(url, new UploadAccepted
                    {
                        DocumentId = string.Join(',', docIds),
                        Index = input.Index,
                        Message = "Document upload completed, ingestion pipeline started"
                    });
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "Document upload failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseGetIndexesEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // List of indexes endpoint
        var route = app.MapGet(Constants.HttpIndexesEndpoint,
                async Task<IResult> (
                    IKernelMemory service,
                    ILogger<WebAPIEndpoint> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New index list HTTP request");

                    var result = new IndexCollection();
                    IEnumerable<IndexDetails> list = await service.ListIndexesAsync(cancellationToken)
                        .ConfigureAwait(false);

                    foreach (IndexDetails index in list)
                    {
                        result.Results.Add(index);
                    }

                    return Results.Ok(result);
                })
            .Produces<IndexCollection>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseDeleteIndexesEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // Delete index endpoint
        var route = app.MapDelete(Constants.HttpIndexesEndpoint,
                async Task<IResult> (
                    [FromQuery(Name = Constants.WebServiceIndexField)]
                    string? index,
                    IKernelMemory service,
                    ILogger<WebAPIEndpoint> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New delete document HTTP request, index '{0}'", index);
                    await service.DeleteIndexAsync(index: index, cancellationToken)
                        .ConfigureAwait(false);
                    // There's no API to check the index deletion progress, so the URL is empty
                    var url = string.Empty;
                    return Results.Accepted(url, new DeleteAccepted
                    {
                        Index = index ?? string.Empty,
                        Message = "Index deletion request received, pipeline started"
                    });
                })
            .Produces<DeleteAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseDeleteDocumentsEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // Delete document endpoint
        var route = app.MapDelete(Constants.HttpDocumentsEndpoint,
                async Task<IResult> (
                    [FromQuery(Name = Constants.WebServiceIndexField)]
                    string? index,
                    [FromQuery(Name = Constants.WebServiceDocumentIdField)]
                    string documentId,
                    IKernelMemory service,
                    ILogger<WebAPIEndpoint> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New delete document HTTP request, index '{0}'", index);
                    await service.DeleteDocumentAsync(documentId: documentId, index: index, cancellationToken)
                        .ConfigureAwait(false);
                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal);
                    return Results.Accepted(url, new DeleteAccepted
                    {
                        DocumentId = documentId,
                        Index = index ?? string.Empty,
                        Message = "Document deletion request received, pipeline started"
                    });
                })
            .Produces<DeleteAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseAskEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // Ask endpoint
        var route = app.MapPost(Constants.HttpAskEndpoint,
                async Task<IResult> (
                    MemoryQuery query,
                    IKernelMemory service,
                    ILogger<WebAPIEndpoint> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New search request, index '{0}', minRelevance {1}", query.Index, query.MinRelevance);
                    MemoryAnswer answer = await service.AskAsync(
                            question: query.Question,
                            index: query.Index,
                            filters: query.Filters,
                            minRelevance: query.MinRelevance,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(answer);
                })
            .Produces<MemoryAnswer>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseSearchEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // Search endpoint
        var route = app.MapPost(Constants.HttpSearchEndpoint,
                async Task<IResult> (
                    SearchQuery query,
                    IKernelMemory service,
                    ILogger<WebAPIEndpoint> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New search HTTP request, index '{0}', minRelevance {1}", query.Index, query.MinRelevance);
                    SearchResult answer = await service.SearchAsync(
                            query: query.Query,
                            index: query.Index,
                            filters: query.Filters,
                            minRelevance: query.MinRelevance,
                            limit: query.Limit,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(answer);
                })
            .Produces<SearchResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseUploadStatusEndpoint(this IEndpointRouteBuilder app, IEndpointFilter? authFilter = null)
    {
        // Document status endpoint
        var route = app.MapGet(Constants.HttpUploadStatusEndpoint,
                async Task<IResult> (
                    [FromQuery(Name = Constants.WebServiceIndexField)]
                    string? index,
                    [FromQuery(Name = Constants.WebServiceDocumentIdField)]
                    string documentId,
                    IKernelMemory memoryClient,
                    ILogger<WebAPIEndpoint> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New document status HTTP request");
                    if (string.IsNullOrEmpty(documentId))
                    {
                        return Results.Problem(detail: $"'{Constants.WebServiceDocumentIdField}' query parameter is missing or has no value", statusCode: 400);
                    }

                    DataPipelineStatus? pipeline = await memoryClient.GetDocumentStatusAsync(documentId: documentId, index: index, cancellationToken)
                        .ConfigureAwait(false);
                    if (pipeline == null)
                    {
                        return Results.Problem(detail: "Document not found", statusCode: 404);
                    }

                    if (pipeline.Empty)
                    {
                        return Results.Problem(detail: "Empty pipeline", statusCode: 404);
                    }

                    return Results.Ok(pipeline);
                })
            .Produces<DataPipelineStatus>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    // Class used to tag log entries and allow log filtering
    // ReSharper disable once ClassNeverInstantiated.Local
#pragma warning disable CA1812 // used by logger, can't be static
    private sealed class WebAPIEndpoint
    {
    }
#pragma warning restore CA1812

    public class HttpAddUrlRequest
    {
        public string Index { get; set; } = string.Empty;
        public string DocumentId { get; set; } = string.Empty;
        public TagCollection Tags { get; set; } = new();
        public List<string> Steps { get; set; } = new();
        public Uri Url { get; set; } = null!;

        /* Resources:
         * https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0
         * https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0#upload-large-files-with-streaming
         * https://stackoverflow.com/questions/71499435/how-do-i-do-file-upload-using-asp-net-core-6-minimal-api
         * https://stackoverflow.com/questions/57033535/multipartformdatacontent-add-stringcontent-is-adding-carraige-return-linefeed-to
         */
        public static async Task<(HttpAddUrlRequest model, bool isValid, string errMsg)> BindHttpRequestAsync(
            HttpRequest httpRequest, CancellationToken cancellationToken = default)
        {
            var result = new HttpAddUrlRequest();

            // Read form
            IFormCollection form = await httpRequest.ReadFormAsync(cancellationToken).ConfigureAwait(false);

            // Only one index can be defined
            if (form.TryGetValue(Constants.WebServiceIndexField, out StringValues indexes) && indexes.Count > 1)
            {
                return (result, false, $"Invalid index name, '{Constants.WebServiceIndexField}', multiple values provided");
            }

            // Only one document ID can be defined
            if (form.TryGetValue(Constants.WebServiceDocumentIdField, out StringValues documentIds) && documentIds.Count > 1)
            {
                return (result, false, $"Invalid document ID, '{Constants.WebServiceDocumentIdField}' must be a single value, not a list");
            }

            // Document Id is optional, e.g. used if the client wants to retry the same upload, otherwise a random/unique one is generated
            string? documentId = documentIds.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(documentId))
            {
                documentId = DateTimeOffset.Now.ToString("yyyyMMdd.HHmmss.", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N");
            }

            // exactly one url needs to be provided
            if (!form.TryGetValue("url", out StringValues urlString) || urlString.Count != 1 || !Uri.TryCreate(urlString.FirstOrDefault(), UriKind.Absolute, out Uri url))
            {
                return (result, false, $"Invalid URL, 'url' must be a single valid URL");
            }

            // Optional document tags. Tags are passed in as "key:value", where a key can have multiple values. See TagCollection.
            if (form.TryGetValue(Constants.WebServiceTagsField, out StringValues tags))
            {
                foreach (string? tag in tags)
                {
                    if (tag == null) { continue; }

                    var keyValue = tag.Trim().Split(Constants.ReservedEqualsChar, 2);
                    string key = keyValue[0].Trim();
                    if (string.IsNullOrWhiteSpace(key)) { continue; }

                    ValidateTagName(key);

                    string? value = keyValue.Length == 1 ? null : keyValue[1].Trim();
                    if (string.IsNullOrWhiteSpace(value)) { value = null; }

                    result.Tags.Add(key, value);
                }
            }

            // Optional pipeline steps. The user can pass a custom list or leave it to the system to use the default.
            if (form.TryGetValue(Constants.WebServiceStepsField, out StringValues steps))
            {
                foreach (string? step in steps)
                {
                    if (string.IsNullOrWhiteSpace(step)) { continue; }

                    // Allow step names to be separated by space, comma, semicolon
                    var list = step.Replace(' ', ';').Replace(',', ';').Split(';');
                    result.Steps.AddRange(from s in list where !string.IsNullOrWhiteSpace(s) select s.Trim());
                }
            }

            result.Index = indexes.FirstOrDefault()?.Trim() ?? string.Empty;
            result.DocumentId = documentId;
            result.Url = url;

            return (result, true, string.Empty);
        }

        private static void ValidateTagName(string tagName)
        {
            if (tagName.StartsWith(Constants.ReservedTagsPrefix, StringComparison.Ordinal))
            {
                throw new KernelMemoryException(
                    $"The tag name prefix '{Constants.ReservedTagsPrefix}' is reserved for internal use.");
            }

            if (tagName is Constants.ReservedDocumentIdTag
                or Constants.ReservedFileIdTag
                or Constants.ReservedFilePartitionTag
                or Constants.ReservedFileTypeTag
                or Constants.ReservedSyntheticTypeTag)
            {
                throw new KernelMemoryException($"The tag name '{tagName}' is reserved for internal use.");
            }
        }
    }

}
