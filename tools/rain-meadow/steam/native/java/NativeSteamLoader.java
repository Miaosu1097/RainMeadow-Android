package rwmeadow.steam;

public final class NativeSteamLoader {
    public void load(String runtime, String valve, String transport) {
        // The declaring class selects the same Android linker namespace for all three libraries.
        System.load(runtime);
        System.load(valve);
        System.load(transport);
    }

    public native long openLoaded(String path);
    public native long resolve(long handle, String symbol);
}
