# `POW_functions`

**Source:** `ollama` (Papyrus Ollama Wrapper) • **Flags:** Hidden

---

## Global Functions

### `GetGenerationResult(callbackId) → String`

**Flags:** Native Global

Retrieves the completed generation result and automatically cleans up storage

Parameters:
  callbackId - The ID returned by OllamaGenerate()

Returns the generated text, or error message if generation failed

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `callbackId` | `Int` | ✓ |  |

### `IsGenerationComplete(callbackId) → Bool`

**Flags:** Native Global

Non-blocking poll to check if a specific generation request has completed
Allows scripts to query status without freezing execution

Parameters:
  callbackId - The ID returned by OllamaGenerate()

Returns true when generation is done and result is ready

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `callbackId` | `Int` | ✓ |  |

### `OllamaGenerate(model, prompt, maxLineWidth) → Int`

**Flags:** Native Global

Starts an asynchronous AI generation request that runs in the background
Configuration is auto-detected based on calling script name:
  - If script is "MyMod_BookGen", looks for "Data/SKSE/Plugins/MyMod_POW.toml"
  - Falls back to POW.toml if no mod-specific config exists

Parameters:
  model - Ollama model name (e.g. "llama3:8b"). If empty, uses model from config file
  prompt - Input text for generation
  maxLineWidth - Characters per line for text wrapping (default = 0 (no wrap), 20 fits properly in vanilla books)

Returns callback ID to track this specific request

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `model` | `String` |  | `""` |
| `prompt` | `String` | ✓ |  |
| `maxLineWidth` | `Int` |  | `0` |
