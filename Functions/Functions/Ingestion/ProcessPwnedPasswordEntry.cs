﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace HaveIBeenPwned.PwnedPasswords.Functions.Ingestion;

public class ProcessPwnedPasswordEntryBatch
{
    private readonly ILogger<ProcessPwnedPasswordEntryBatch> _log;
    private readonly ITableStorage _tableStorage;
    private readonly IFileStorage _blobStorage;

    /// <summary>
    /// Pwned Passwords - Append handler
    /// </summary>
    /// <param name="blobStorage">The Blob storage</param>
    public ProcessPwnedPasswordEntryBatch(ILogger<ProcessPwnedPasswordEntryBatch> log, ITableStorage tableStorage, IFileStorage blobStorage)
    {
        _log = log;
        _tableStorage = tableStorage;
        _blobStorage = blobStorage;
    }

    [FunctionName("ProcessAppendQueueItem")]
    public async Task Run([QueueTrigger("%TableNamespace%-ingestion", Connection = "PwnedPasswordsConnectionString")] byte[] queueItem, CancellationToken cancellationToken)
    {
        PasswordEntryBatch? batch = JsonSerializer.Deserialize<PasswordEntryBatch>(Encoding.UTF8.GetString(queueItem));
        if (batch != null)
        {
            // Let's set some activity tags and log scopes so we have event correlation in our logs!
            Activity.Current?.AddTag("SubscriptionId", batch.SubscriptionId).AddTag("TransactionId", batch.TransactionId);
            foreach (KeyValuePair<string, List<PwnedPasswordsIngestionValue>> prefixBatch in batch.PasswordEntries)
            {
                List<Task> tasks = new(2 + prefixBatch.Value.Count);
                foreach (PwnedPasswordsIngestionValue item in prefixBatch.Value)
                {
                    tasks.Add(IncrementHashEntry(batch, item));
                }

                tasks.Add(_tableStorage.MarkHashPrefixAsModified(prefixBatch.Key));
                tasks.Add(UpdateHashfile(batch, prefixBatch.Key, prefixBatch.Value));
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        async Task IncrementHashEntry(PasswordEntryBatch batch, PwnedPasswordsIngestionValue item, CancellationToken cancellationToken = default)
        {
            while (!await _tableStorage.AddOrIncrementHashEntry(batch, item, cancellationToken).ConfigureAwait(false))
            {
            }
        }

        async Task UpdateHashfile(PasswordEntryBatch batch, string prefix, List<PwnedPasswordsIngestionValue> batchEntries, CancellationToken cancellationToken = default)
        {
            bool blobUpdated = false;
            while (!blobUpdated)
            {
                try
                {
                    blobUpdated = await ParseAndUpdateHashFile(batch, prefix, batchEntries, cancellationToken).ConfigureAwait(false);
                    if (!blobUpdated)
                    {
                        _log.LogWarning("Subscription {SubscriptionId} failed to updated blob {HashPrefix} as part of transaction {TransactionId}! Will retry!", batch.SubscriptionId, prefix, batch.TransactionId);
                    }
                }
                catch (FileNotFoundException)
                {
                    _log.LogError("Subscription {SubscriptionId} is unable to find a hash file with prefix {prefix} as part of transaction {TransactionId}. Something is wrong as this shouldn't happen!", batch.SubscriptionId, prefix, batch.TransactionId);
                    return;
                }
            }

            _log.LogInformation("Subscription {SubscriptionId} successfully updated blob {HashPrefix} as part of transaction {TransactionId}!", batch.SubscriptionId, prefix, batch.TransactionId);
        }
    }

    private async Task<bool> ParseAndUpdateHashFile(PasswordEntryBatch batch, string prefix, List<PwnedPasswordsIngestionValue> batchEntries, CancellationToken cancellationToken = default)
    {
        PwnedPasswordsFile blobFile = await _blobStorage.GetHashFileAsync(prefix, "sha1", cancellationToken).ConfigureAwait(false);

        // Let's read the existing blob into a sorted dictionary so we can write it back in order!
        SortedDictionary<string, int> hashes = ParseHashFile(blobFile);

        // We now have a sorted dictionary with the hashes for this prefix.
        // Let's add or update the suffixes with the prevalence counts.
        foreach (PwnedPasswordsIngestionValue item in batchEntries)
        {
            string suffix = item.SHA1Hash[5..];
            if (hashes.ContainsKey(suffix))
            {
                hashes[suffix] = hashes[suffix] + item.Prevalence;
                _log.LogInformation("Subscription {SubscriptionId} updating suffix {HashSuffix} in blob {HashPrefix} from {PrevalenceBefore} to {PrevalenceAfter} as part of transaction {TransactionId}!", batch.SubscriptionId, suffix, prefix, hashes[suffix] - item.Prevalence, hashes[suffix], batch.TransactionId);
            }
            else
            {
                hashes.Add(suffix, item.Prevalence);
                _log.LogInformation("Subscription {SubscriptionId} adding new suffix {HashSuffix} to blob {HashPrefix} with {Prevalence} as part of transaction {TransactionId}!", batch.SubscriptionId, suffix, prefix, item.Prevalence, batch.TransactionId);
            }
        }

        // Now let's try to update the current blob with the new prevalence count!
        return await _blobStorage.UpdateHashFileAsync(prefix, hashes, blobFile.ETag, cancellationToken).ConfigureAwait(false);
    }

    private static SortedDictionary<string, int> ParseHashFile(PwnedPasswordsFile blobFile)
    {
        var hashes = new SortedDictionary<string, int>();
        using (var reader = new StreamReader(blobFile.Content))
        {
            string? hashLine = reader.ReadLine();
            while (hashLine != null)
            {
                // Let's make sure we can parse this as a proper hash!
                if (!string.IsNullOrEmpty(hashLine) && hashLine.Length >= 37 && hashLine[35] == ':' && int.TryParse(hashLine[36..].Replace(",", ""), out int currentPrevalence) && currentPrevalence > 0)
                {
                    hashes.Add(hashLine[..35], currentPrevalence);
                }

                hashLine = reader.ReadLine();
            }
        }

        return hashes;
    }
}
