using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KernelMemory.Prompts;
using Microsoft.KernelMemory.Search;

internal class ViuSearchClient : SearchClient
{
    private readonly IMemoryDb _memoryDb;
    private readonly ITextGenerator _textGenerator;
    private readonly SearchClientConfig _config;
    private readonly ILogger<SearchClient> _log;
    private readonly string _answerPrompt;
    public ViuSearchClient(IMemoryDb memoryDb, ITextGenerator textGenerator, SearchClientConfig? config = null, IPromptProvider? promptProvider = null, ILogger<SearchClient>? log = null) : base(memoryDb, textGenerator, config, promptProvider, log)
    {
        this._memoryDb = memoryDb;
        this._textGenerator = textGenerator;
        this._config = config ?? new SearchClientConfig();
        this._config.Validate();

        promptProvider ??= new EmbeddedPromptProvider();
        this._answerPrompt = promptProvider.ReadPrompt(Constants.PromptNamesAnswerWithFacts);

        this._log = log ?? DefaultLogger<SearchClient>.Instance;

        if (this._memoryDb == null)
        {
            throw new KernelMemoryException("Search memory DB not configured");
        }

        if (this._textGenerator == null)
        {
            throw new KernelMemoryException("Text generator not configured");
        }
    }

    public async Task<MemoryAnswer> AskAsync(
        string index,
        string question,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        CancellationToken cancellationToken = default)
    {
        var noAnswerFound = new MemoryAnswer
        {
            Question = question,
            NoResult = true,
            Result = this._config.EmptyAnswer,
        };

        if (string.IsNullOrEmpty(question))
        {
            this._log.LogWarning("No question provided");
            noAnswerFound.NoResultReason = "No question provided";
            return noAnswerFound;
        }

        var facts = new StringBuilder();
        var maxTokens = this._config.MaxAskPromptSize > 0
            ? this._config.MaxAskPromptSize
            : this._textGenerator.MaxTokenTotal;
        var tokensAvailable = maxTokens
                              - this._textGenerator.CountTokens(this._answerPrompt)
                              - this._textGenerator.CountTokens(question)
                              - this._config.AnswerTokens;

        var factsUsedCount = 0;
        var factsAvailableCount = 0;
        var answer = noAnswerFound;

        this._log.LogTrace("Fetching relevant memories");
        IAsyncEnumerable<(MemoryRecord, double)> matches = this._memoryDb.GetSimilarListAsync(
            index: index,
            text: question,
            filters: filters,
            minRelevance: minRelevance,
            limit: this._config.MaxMatchesCount,
            withEmbeddings: false,
            cancellationToken: cancellationToken);

        // Memories are sorted by relevance, starting from the most relevant
        await foreach ((MemoryRecord memory, double relevance) in matches.ConfigureAwait(false))
        {
            // Note: a document can be composed by multiple files
            string documentId = memory.GetDocumentId(this._log);

            // Identify the file in case there are multiple files
            string fileId = memory.GetFileId(this._log);

            // Note: this is not a URL and perhaps could be dropped. For now it acts as a unique identifier. See also SourceUrl.
            string linkToFile = $"{index}/{documentId}/{fileId}";

            string fileName = memory.GetFileName(this._log);

            string webPageUrl = memory.GetWebPageUrl();

            var partitionText = memory.GetPartitionText(this._log).Trim();
            if (string.IsNullOrEmpty(partitionText))
            {
                this._log.LogError("The document partition is empty, doc: {0}", memory.Id);
                continue;
            }

            factsAvailableCount++;

            // TODO: add file age in days, to push relevance of newer documents
            var fact = $"==== [File:{(fileName == "comtemt.url" ? webPageUrl : fileName)};Relevance:{relevance:P1}]:\n{partitionText}\n";

            // Use the partition/chunk only if there's room for it
            var size = this._textGenerator.CountTokens(fact);
            if (size >= tokensAvailable)
            {
                // Stop after reaching the max number of tokens
                break;
            }

            factsUsedCount++;
            this._log.LogTrace("Adding text {0} with relevance {1}", factsUsedCount, relevance);

            facts.Append(fact);
            tokensAvailable -= size;

            // If the file is already in the list of citations, only add the partition
            var citation = answer.RelevantSources.FirstOrDefault(x => x.Link == linkToFile);
            if (citation == null)
            {
                citation = new Citation();
                answer.RelevantSources.Add(citation);
            }

            // Add the partition to the list of citations
            citation.Index = index;
            citation.DocumentId = documentId;
            citation.FileId = fileId;
            citation.Link = linkToFile;
            citation.SourceContentType = memory.GetFileContentType(this._log);
            citation.SourceName = fileName;
            citation.SourceUrl = webPageUrl;

            citation.Partitions.Add(new Citation.Partition
            {
                Text = partitionText,
                Relevance = (float)relevance,
                PartitionNumber = memory.GetPartitionNumber(this._log),
                SectionNumber = memory.GetSectionNumber(),
                LastUpdate = memory.GetLastUpdate(),
                Tags = memory.Tags,
            });

            // In cases where a buggy storage connector is returning too many records
            if (factsUsedCount >= this._config.MaxMatchesCount)
            {
                break;
            }
        }

        if (factsAvailableCount > 0 && factsUsedCount == 0)
        {
            this._log.LogError("Unable to inject memories in the prompt, not enough tokens available");
            noAnswerFound.NoResultReason = "Unable to use memories";
            return noAnswerFound;
        }

        if (factsUsedCount == 0)
        {
            this._log.LogWarning("No memories available");
            noAnswerFound.NoResultReason = "No memories available";
            return noAnswerFound;
        }

        var text = new StringBuilder();
        var charsGenerated = 0;
        var watch = new Stopwatch();
        watch.Restart();
        await foreach (var x in this.GenerateAnswerAsync(question, facts.ToString())
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            text.Append(x);

            if (this._log.IsEnabled(LogLevel.Trace) && text.Length - charsGenerated >= 30)
            {
                charsGenerated = text.Length;
                this._log.LogTrace("{0} chars generated", charsGenerated);
            }
        }

        watch.Stop();

        answer.Result = text.ToString();
        answer.NoResult = ValueIsEquivalentTo(answer.Result, this._config.EmptyAnswer);
        if (answer.NoResult)
        {
            answer.NoResultReason = "No relevant memories found";
            this._log.LogTrace("Answer generated in {0} msecs. No relevant memories found", watch.ElapsedMilliseconds);
        }
        else
        {
            this._log.LogTrace("Answer generated in {0} msecs", watch.ElapsedMilliseconds);
        }

        return answer;
    }

    private IAsyncEnumerable<string> GenerateAnswerAsync(string question, string facts)
    {
        var prompt = this._answerPrompt;
        prompt = prompt.Replace("{{$facts}}", facts.Trim(), StringComparison.OrdinalIgnoreCase);

        question = question.Trim();
        question = question.EndsWith('?') ? question : $"{question}?";
        prompt = prompt.Replace("{{$input}}", question, StringComparison.OrdinalIgnoreCase);

        prompt = prompt.Replace("{{$notFound}}", this._config.EmptyAnswer, StringComparison.OrdinalIgnoreCase);

        var options = new TextGenerationOptions
        {
            Temperature = this._config.Temperature,
            TopP = this._config.TopP,
            PresencePenalty = this._config.PresencePenalty,
            FrequencyPenalty = this._config.FrequencyPenalty,
            MaxTokens = this._config.AnswerTokens,
            StopSequences = this._config.StopSequences,
            TokenSelectionBiases = this._config.TokenSelectionBiases,
        };

        if (this._log.IsEnabled(LogLevel.Debug))
        {
            this._log.LogDebug("Running RAG prompt, size: {0} tokens, requesting max {1} tokens",
                this._textGenerator.CountTokens(prompt),
                this._config.AnswerTokens);
        }

        return this._textGenerator.GenerateTextAsync(prompt, options);
    }

    private static bool ValueIsEquivalentTo(string value, string target)
    {
        value = value.Trim().Trim('.', '"', '\'', '`', '~', '!', '?', '@', '#', '$', '%', '^', '+', '*', '_', '-', '=', '|', '\\', '/', '(', ')', '[', ']', '{', '}', '<', '>');
        target = target.Trim().Trim('.', '"', '\'', '`', '~', '!', '?', '@', '#', '$', '%', '^', '+', '*', '_', '-', '=', '|', '\\', '/', '(', ')', '[', ']', '{', '}', '<', '>');
        return string.Equals(value, target, StringComparison.OrdinalIgnoreCase);
    }
}
