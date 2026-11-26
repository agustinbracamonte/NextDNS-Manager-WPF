namespace NextDNSManager.Service;

/// <summary>Restarts the existing broker after failed CLI/DNS removal work.</summary>
internal static class ServiceRemovalRecovery
{
    internal static void Run(bool brokerExisted, Action removeOwnedClient, Action restartBroker)
    {
        ArgumentNullException.ThrowIfNull(removeOwnedClient);
        ArgumentNullException.ThrowIfNull(restartBroker);

        try
        {
            removeOwnedClient();
        }
        catch (Exception removalError) when (removalError is not OutOfMemoryException)
        {
            if (!brokerExisted) throw;

            try
            {
                restartBroker();
            }
            catch (Exception restartError) when (restartError is not OutOfMemoryException)
            {
                throw new AggregateException(
                    "Falló la limpieza DNS/CLI y no se pudo reiniciar el broker. Se conservó la copia de DNS para reparar la instalación.",
                    removalError,
                    restartError);
            }

            throw new InvalidOperationException(
                "No se completó la limpieza DNS/CLI. El broker se reinició para reintentar y la copia de recuperación se conservó.",
                removalError);
        }
    }
}
