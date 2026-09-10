using Windows.Devices.Bluetooth.Advertisement;

namespace DDJ200.CodexBridge;

internal sealed record BluetoothCandidate(ulong Address,string Name,short Signal)
{
    public override string ToString()=>$"{Name} — endereço {Address:X12} — sinal {Signal} dBm";
}

internal sealed class BluetoothConnectionForm : Form
{
    private readonly BluetoothLEAdvertisementWatcher watcher=new(){ScanningMode=BluetoothLEScanningMode.Active};
    private readonly ListBox devices=new(){Dock=DockStyle.Fill};
    private readonly Label state=new(){Dock=DockStyle.Top,Height=48,Padding=new Padding(8),Text="Procurando DDJ-200 por perto..."};
    private readonly Button choose=new(){Text="Conectar este DDJ-200",AutoSize=true,Enabled=false};
    private readonly Dictionary<ulong,BluetoothCandidate> found=[];
    public ulong? SelectedAddress{get;private set;}
    public string? SelectedName{get;private set;}

    public BluetoothConnectionForm()
    {
        Text="Conexão Bluetooth DDJ-200";Width=560;Height=420;StartPosition=FormStartPosition.CenterScreen;MinimizeBox=false;
        // Some DDJ-200 advertisements expose the name without including the
        // BLE-MIDI UUID. Discovery filters by exact product-name prefix; the
        // bridge still validates service, characteristic and capabilities on connect.
        watcher.Received+=Received;watcher.Stopped+=Stopped;
        devices.SelectedIndexChanged+=(_,_)=>choose.Enabled=devices.SelectedItem is BluetoothCandidate;
        choose.Click+=(_,_)=>{if(devices.SelectedItem is BluetoothCandidate item){SelectedAddress=item.Address;SelectedName=item.Name;DialogResult=DialogResult.OK;Close();}};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=52,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8)};
        buttons.Controls.Add(choose);buttons.Controls.Add(new Button{Text="Cancelar",AutoSize=true,DialogResult=DialogResult.Cancel});
        Controls.Add(devices);Controls.Add(new Label{Dock=DockStyle.Top,Height=52,Padding=new Padding(8),Text="Ligue a DDJ-200 e deixe o Bluetooth disponível. Selecione somente a sua DDJ; não é necessário digitar PIN no Windows."});Controls.Add(state);Controls.Add(buttons);
        Shown+=(_,_)=>StartScan();FormClosed+=(_,_)=>DisposeWatcher();
    }

    private void StartScan()
    {
        try{watcher.Start();state.Text="Procurando... O nome precisa começar com DDJ-200.";}
        catch(Exception ex){state.Text="Não foi possível iniciar a busca: "+Friendly(ex);}
    }
    private void Received(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string name=args.Advertisement.LocalName?.Trim()??"";if(!name.StartsWith("DDJ-200",StringComparison.OrdinalIgnoreCase))return;
        BeginInvoke(() =>
        {
            found[args.BluetoothAddress]=new(args.BluetoothAddress,name,args.RawSignalStrengthInDBm);
            var selected=(devices.SelectedItem as BluetoothCandidate)?.Address;devices.Items.Clear();
            foreach(var item in found.Values.OrderByDescending(x=>x.Signal))devices.Items.Add(item);
            if(selected.HasValue)for(int i=0;i<devices.Items.Count;i++)if(((BluetoothCandidate)devices.Items[i]).Address==selected){devices.SelectedIndex=i;break;}
            state.Text=$"{devices.Items.Count} DDJ-200 encontrada(s).";
        });
    }
    private void Stopped(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementWatcherStoppedEventArgs args)=>BeginInvoke(()=>state.Text=args.Error.ToString()=="Success"?"Busca encerrada.":"Busca interrompida. Desligue e ligue o Bluetooth e tente novamente.");
    private void DisposeWatcher(){if(watcher.Status==BluetoothLEAdvertisementWatcherStatus.Started)watcher.Stop();watcher.Received-=Received;watcher.Stopped-=Stopped;}
    private static string Friendly(Exception ex)=>ex is UnauthorizedAccessException?"permita acesso ao Bluetooth para este aplicativo.":"verifique se o Bluetooth do computador está ligado.";
}
