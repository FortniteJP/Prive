namespace AFortOnlineBeacon.Net.Channels;

public class FControlChannelOutBunch : FOutBunch {
    public FControlChannelOutBunch(UChannel inChannel, bool bClose) : base(inChannel, bClose) => bReliable = true;
}