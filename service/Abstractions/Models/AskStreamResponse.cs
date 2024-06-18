// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;

namespace Microsoft.KernelMemory;

public class AskStreamResponse
{
    [JsonPropertyName("askId")]
    public string AskId { get; set; } = string.Empty;
}
