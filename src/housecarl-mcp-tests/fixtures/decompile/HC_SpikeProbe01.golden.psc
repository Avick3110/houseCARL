ScriptName HC_SpikeProbe01 extends Quest Hidden
{Spike probe: one of each Papyrus construct, compiled to study codegen patterns.}

int backing = 100
int[] numbers
Actor watcher
bool seenIt = false
string label = "probe"

float Property Pi = 3.14 AutoReadOnly
{Docstring on a property.}

int Property Speed = 7 Auto

int Property Wrapped
    int Function Get()
        return backing
    EndFunction
    Function Set(int v)
        backing = v + 1
    EndFunction
EndProperty

Event OnInit()
    numbers = new int[5]
    RegisterForSingleUpdate(2.5)
EndEvent

Event OnUpdate()
    int total = Tally(numbers, 3)
    if total > 10 && seenIt
        debug.Trace("big: " + total, 0)
    elseif total < 0 || !seenIt
        seenIt = true
    else
        total = total - 1
    endif
    GotoState("Busy")
EndEvent

int Function Tally(int[] arr, int bonus)
    {Adds everything plus a bonus.}
    int i = 0
    int sum = 0
    while i < arr.Length
        sum = sum + arr[i]
        i = i + 1
    endwhile
    float scaled = sum * 1.5 + bonus
    if scaled > 100.0
        return 100
    endif
    return (scaled as int)
EndFunction

Function Poke(string msg, int times)
    int k = 0
    while k < times
        debug.Trace(msg + " #" + k, 0)
        k = k + 1
    endwhile
EndFunction

Function Tick()
    Poke("hi", 1)
EndFunction

int Function Doubler(int x) Global
    return x * 2
EndFunction

Auto State Idle
    Function Tick()
        Poke("Idle", 2)
        watcher = game.GetPlayer()
        int slot = numbers.Find(7)
        int neg = -slot
        if slot >= 0
            numbers[slot] = numbers[slot] % 4 + neg
        endif
    EndFunction

EndState

State Busy
    Function Tick()
        float w = utility.RandomFloat(0.0, Self.Pi)
        label = label + ": " + w
        GotoState("Idle")
    EndFunction

EndState

