# FoundryIQ — Kako da ubacis u postojeci kod

## 1. AIVideoCreator.xaml.cs — dodaj field i inicijalizaciju

Negdje na pocetku klase (gdje vec imas `OllamaClient _ollama`), dodaj:

```csharp
// Foundry IQ — Sloj 0 u query pipelinu (accessibility knowledge)
private FoundryIQClient _foundryIQ;
```

U konstruktoru ili u metodi gdje ucitavas API kljuceve, dodaj:

```csharp
// Ucitaj Foundry IQ config (isto gdje ucitavas Pixabay key itd.)
var fiqConfig = LoadFoundryIQConfig(); // vidi tacku 3
_foundryIQ = new FoundryIQClient(
    endpoint:        fiqConfig.Endpoint,
    apiKey:          fiqConfig.ApiKey,
    knowledgeBaseId: fiqConfig.KnowledgeBaseId
);
```


## 2. AIVideoCreator.xaml.cs — u scene loop gdje vec pozivas Ollamu

Trazi u kodu ovakav pattern (negdje u glavnoj petlji po stihovima):

```csharp
// Ovako SADA izgleda (primjer):
var tagType  = StrictQueryEngine.ClassifyLyric(lyric);
var sentiment = StrictQueryEngine.ClassifySentiment(lyric);
string prompt = StrictQueryEngine.BuildOllamaPrompt(lyric, ctx, tagType, sentiment, needsCloseUp);
string ollamaQuery = await _ollama.GenerateAsync(prompt);
```

Promijeni u:

```csharp
var tagType   = StrictQueryEngine.ClassifyLyric(lyric);
var sentiment = StrictQueryEngine.ClassifySentiment(lyric);

// SLOJ 0: Foundry IQ accessibility hint (ne blokira ako nije dostupan)
string fiqHint = null;
if (_foundryIQ?.IsConfigured == true)
{
    fiqHint = await _foundryIQ.GetAccessibilityHintAsync(
        lyric, sentiment, tagType, ctx.AgeGroup, ct);

    // Ako Foundry IQ vratio hint za audio opis scene — logiraj ga
    if (!string.IsNullOrWhiteSpace(fiqHint))
        LogMessage($"♿ FoundryIQ hint: {fiqHint}");
}

// SLOJ 1: Ollama — prima Foundry IQ hint kao accessibility context
string prompt = StrictQueryEngine.BuildOllamaPromptWithFIQ(
    lyric, ctx, tagType, sentiment, needsCloseUp, fiqHint);
string ollamaQuery = await _ollama.GenerateAsync(prompt);
```


## 3. StrictQueryEngine.cs — dodaj BuildOllamaPromptWithFIQ metodu

Na kraju klase StrictQueryEngine (prije zatvaranja '}'}, dodaj:

```csharp
/// <summary>
/// Prosirena verzija BuildOllamaPrompt sa Foundry IQ accessibility hintom.
/// Ako accessibilityHint je null — ponasa se identično kao originalna metoda.
/// </summary>
public static string BuildOllamaPromptWithFIQ(
    string lyric,
    SongContext ctx,
    LyricTagType tagType       = LyricTagType.Narrative,
    SentimentPolarity sentiment = SentimentPolarity.Neutral,
    bool needsCloseUp          = false,
    string accessibilityHint   = null)
{
    // Pocni sa originalnim promptom (nista nije promijenjeno)
    string basePrompt = BuildOllamaPrompt(lyric, ctx, tagType, sentiment, needsCloseUp);

    // Ako nema Foundry IQ hinta — vrati originalni prompt (sistem radi kao prije)
    if (string.IsNullOrWhiteSpace(accessibilityHint))
        return basePrompt;

    // Ubaci accessibility context PRIJE finalnog pitanja
    // Cilj: Ollama bira vizuale koji su opisni i pristupacni
    string accessibilitySection =
        $"\nACCESSIBILITY CONTEXT (from Microsoft Foundry IQ knowledge base):\n" +
        $"{accessibilityHint}\n" +
        $"GUIDELINE: Prefer visually descriptive scenes that can be understood " +
        $"through audio description. Avoid fast-cut abstract sequences.\n";

    // Ubaci accessibility sekciju ispred zadnje linije prompta ("Query (write ONLY...)")
    int insertPos = basePrompt.LastIndexOf("Query (write ONLY");
    if (insertPos < 0)
        return basePrompt + accessibilitySection; // fallback ako se ne nadje marker

    return basePrompt.Insert(insertPos, accessibilitySection);
}
```


## 4. ApiKeyDialog.xaml / ApiKeyDialog.xaml.cs — Foundry IQ polja

U ApiKeyDialog.xaml, gdje vec imas Pixabay API key polje, dodaj ispod:

```xml
<!-- Foundry IQ -->
<TextBlock Text="Microsoft Foundry IQ (opciono — accessibility layer)"
           Foreground="#888" Margin="0,15,0,5"/>
<TextBox x:Name="txtFoundryEndpoint"
         PlaceholderText="https://YOUR-RESOURCE.services.ai.azure.com"
         AutomationProperties.Name="Foundry IQ endpoint URL"
         AutomationProperties.HelpText="Azure AI Foundry resource endpoint za accessibility knowledge base"/>
<TextBox x:Name="txtFoundryApiKey"
         PlaceholderText="Foundry IQ API Key"
         AutomationProperties.Name="Foundry IQ API key"/>
<TextBox x:Name="txtFoundryKbId"
         PlaceholderText="Knowledge Base ID"
         AutomationProperties.Name="Foundry IQ Knowledge Base ID"/>
<CheckBox x:Name="chkFoundryEnabled"
          Content="Aktiviraj Foundry IQ accessibility sloj"
          AutomationProperties.Name="Aktiviraj Foundry IQ integraciju"/>
```

U ApiKeyDialog.xaml.cs, dodaj save/load logiku za ova polja
(isti pattern kao za Pixabay key — Settings ili Properties.Settings.Default).


## 5. LoadFoundryIQConfig helper metoda

```csharp
private FoundryIQConfig LoadFoundryIQConfig()
{
    return new FoundryIQConfig
    {
        Endpoint        = Properties.Settings.Default.FoundryIQEndpoint  ?? "",
        ApiKey          = Properties.Settings.Default.FoundryIQApiKey    ?? "",
        KnowledgeBaseId = Properties.Settings.Default.FoundryIQKbId      ?? "",
        Enabled         = Properties.Settings.Default.FoundryIQEnabled
    };
}
```


## Rezultat

Kada Foundry IQ NIJE konfigurisan (prazan API key):
→ Sistem radi IDENTIČNO kao prije. Nula promjena u ponasanju.

Kada Foundry IQ JESTE konfigurisan:
→ Svaki stih dobija accessibility hint iz knowledge base
→ Ollama birá pristupacnije vizuale
→ Log pokazuje: ♿ FoundryIQ hint: ...
→ Hackaton uslov ispunjen: integracija Microsoft IQ layera ✅


## Za hackaton — Knowledge Base sadrzaj

Na Microsoft Foundry portalu, napravis knowledge base i ubacis ove dokumente:
- WCAG 2.2 smjernice (PDF, javno dostupne)
- Microsoft Accessibility Guidelines za video sadrzaj
- Audio description best practices (ACAS standard)
- Primjeri pristupacnih vizuelnih metafora (sam napravis .txt fajl)

Sve besplatno, sve javno dostupno, sve legitimno.
