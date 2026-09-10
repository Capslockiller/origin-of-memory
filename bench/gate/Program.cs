// yazan: codex · gpt-6
// Measurement only: inspect the shipped tokenizer and gate, never reimplement ranking.
using System.Reflection;
using System.Text.Json;
using Oom.Contracts;

var vault = args[0];
var batch = args[1];
var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, Top: 5, TotalChars: 7500));
object Call(string method, params object[] values) => typeof(Retrieve)
    .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(retrieve, values)!;
var notes = (IReadOnlyList<Note>)Call("LoadCorpus");
var fold = new TurkishFold();
var tokens = notes.Select(note => fold.Tokenize(new Notes().IndexableText(note)).ToHashSet()).ToArray();
var rows = new List<object>();
foreach (var line in File.ReadLines(batch))
{
    using var doc = JsonDocument.Parse(line);
    var row = doc.RootElement;
    var id = row.GetProperty("id").GetString()!;
    var prompt = row.GetProperty("soru").GetString()!;
    var terms = (string[])Call("QueryTerms", prompt);
    var words = (string[])Call("ContentWords", prompt);
    var ranked = retrieve.Rank(prompt, notes).Take(5).ToArray();
    var hook = retrieve.Hook(prompt, Guid.NewGuid().ToString());
    rows.Add(new {
        id, terms, contentWords = words,
        gateReason = typeof(Retrieve).GetMethod("GateReason", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(retrieve, [prompt]),
        frequencies = terms.ToDictionary(term => term, term => tokens.Count(set => set.Contains(term))),
        ranked = ranked.Select(hit => new {
            name = hit.Name, score = hit.Score, mean = hit.Score / terms.Length,
            overlap = words.Count(word => ((HashSet<string>)Call("GateSurface", hit.Name)).Contains(word)),
            matching = words.Where(word => ((HashSet<string>)Call("GateSurface", hit.Name)).Contains(word)).ToArray(),
            currentGate = retrieve.ShouldInject(prompt, hit)
        }).ToArray(),
        hook = hook.Hits.Select(hit => hit.Name).ToArray()
        , query = retrieve.Query(prompt, Guid.NewGuid().ToString(), 5).Hits
            .Select(hit => new { name = hit.Name, score = hit.Score }).ToArray()
    });
}
Console.WriteLine(JsonSerializer.Serialize(new { yazan = "codex", model = "gpt-6", documents = notes.Count, rows }));
