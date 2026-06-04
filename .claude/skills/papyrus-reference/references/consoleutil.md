# `ConsoleUtil`

**Source:** `consoleutil` (ConsoleUtil)

---

## Global Functions

### `ExecuteCommand(a_command)`

**Flags:** Native Global

@brief Executes the command.
@param a_command - The command to execute, i.e. "player.setav attackdamagemult 100".

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_command` | `String` | ✓ |  |

### `GetSelectedReference() → ObjectReference`

**Flags:** Native Global

@brief Returns the console's selected reference.
@return Returns NONE if no reference is selected, else returns the console's selected reference.

### `GetVersion() → Int`

**Flags:** Native Global

@brief Returns the API version.
@return Returns 0 if not installed, else returns the API version.

### `PrintMessage(a_message)`

**Flags:** Native Global

@brief Prints the given message to the console.
@param a_message - The message to print to the console.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_message` | `String` | ✓ |  |

### `ReadMessage() → String`

**Flags:** Native Global

@brief Reads the last message printed to the console.
@return The last message printed to the console.

### `SetSelectedReference(a_reference)`

**Flags:** Native Global

@brief Sets the console's selected reference to the specified reference.
@param a_reference - The reference to set the selected reference to.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `a_reference` | `ObjectReference` | ✓ |  |
