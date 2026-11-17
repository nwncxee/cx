namespace CX.Engine;

/// <summary>A creature's associate type, read as the engine reads it.</summary>
public readonly unsafe partial struct CNWCCreature
{
    /// <summary><c>ASSOCIATE_TYPE_FAMILIAR</c>, the type the stock inventory key refuses to the player's creature.</summary>
    private const int AssociateFamiliar = 3;

    /// <summary>An associate of any kind (henchman, companion, familiar, summon), whose master is <see cref="MasterId"/>.</summary>
    public bool IsAnyAssociate
    {
        get
        {
#pragma warning disable RS0030 // the associate concepts are the readers of the raw type
            return IsAssociate != 0;
#pragma warning restore RS0030
        }
    }

    /// <summary>A familiar: the player's own creature is one while the player possesses it.</summary>
    public bool IsFamiliar
    {
        get
        {
#pragma warning disable RS0030 // the associate concepts are the readers of the raw type
            return IsAssociate == AssociateFamiliar;
#pragma warning restore RS0030
        }
    }
}
