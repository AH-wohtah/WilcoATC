namespace FreqWatch.Atc.Planning;

/// <summary>Détient le plan de vol courant (null si aucun) et notifie les changements.</summary>
public sealed class FlightPlanStore
{
    public FlightPlan? Current { get; private set; }

    public event Action<FlightPlan?>? Changed;

    public void Set(FlightPlan? plan)
    {
        Current = plan;
        Changed?.Invoke(plan);
    }
}
