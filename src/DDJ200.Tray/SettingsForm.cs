using System.Text.Json;
using System.Text.Json.Nodes;

namespace DDJ200.CodexBridge;

internal sealed class SettingsForm : Form
{
    private static readonly string[] Targets = ["AG00","AG01","AG02","AG03","AG04","AG05","ACT06","ACT07","ACT08","ACT09","ACT10_ACT11","ACT12","joystick.up","joystick.down"];
    private static readonly Dictionary<string,string> TargetNames = new()
    {
        ["AG00"]="Slot Micro 1",["AG01"]="Slot Micro 2",["AG02"]="Slot Micro 3",["AG03"]="Slot Micro 4",["AG04"]="Slot Micro 5",["AG05"]="Slot Micro 6",
        ["joystick.up"]="Seta para cima (Micro)",["joystick.down"]="Seta para baixo (Micro)",
        ["ACT06"]="Tecla Micro 1",["ACT07"]="Tecla Micro 2",["ACT08"]="Tecla Micro 3",["ACT09"]="Tecla Micro 4",["ACT10_ACT11"]="Microfone",["ACT12"]="Codex"
    };
    private static readonly Dictionary<string,string> StateNames = new()
    {
        ["selected"]="Tarefa selecionada",["working"]="Trabalhando",["unread"]="Concluída não lida",["idle"]="Ociosa",["unassigned"]="Sem tarefa",["attention"]="Precisa de atenção",["error"]="Erro",
        ["commandConfigured"]="Comando configurado",["commandHold"]="Comando pressionado",["commandReleased"]="Comando solto"
    };
    private static readonly Dictionary<string,string> ControlNames = new()
    {
        ["deck1.play"]="Deck esquerdo — PLAY",["deck1.cue"]="Deck esquerdo — CUE",
        ["deck1.pad2"]="Deck esquerdo — Pad 2",["deck1.pad3"]="Deck esquerdo — Pad 3",["deck1.pad5"]="Deck esquerdo — Pad 5",["deck1.pad6"]="Deck esquerdo — Pad 6",["deck1.pad7"]="Deck esquerdo — Pad 7",["deck1.pad8"]="Deck esquerdo — Pad 8",
        ["deck2.pad1"]="Deck direito — Pad 1",["deck2.pad2"]="Deck direito — Pad 2",["deck2.pad3"]="Deck direito — Pad 3",["deck2.pad4"]="Deck direito — Pad 4",["deck2.play"]="Deck direito — PLAY",["deck2.cue"]="Deck direito — CUE"
    };
    private readonly string defaultsPath;
    private readonly Func<Task<(string Control,string Signature)?>> identifyControl;
    private JsonObject working;
    private readonly DataGridView mappings = new() { Dock=DockStyle.Fill, AutoGenerateColumns=false, AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false };
    private readonly DataGridView lights = new() { Dock=DockStyle.Fill, AutoGenerateColumns=false, AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false };
    private readonly NumericUpDown leftJog = new() { DecimalPlaces=2, Increment=.05M, Minimum=.05M, Maximum=1, Width=90 };
    private readonly NumericUpDown rightJog = new() { DecimalPlaces=2, Increment=.05M, Minimum=.05M, Maximum=1, Width=90 };
    private readonly Panel lightPreview=new(){Width=34,Height=34,BackColor=Color.FromArgb(40,40,40),Margin=new Padding(8,1,12,1)};
    private readonly System.Windows.Forms.Timer previewTimer=new();
    private int[] previewDurations=[]; private int previewIndex;
    public string? SerializedProfile { get; private set; }

    public SettingsForm(string currentPath, string defaultsPath, Func<Task<(string Control,string Signature)?>> identifyControl)
    {
        this.defaultsPath=defaultsPath;
        this.identifyControl=identifyControl;
        working=JsonNode.Parse(File.ReadAllText(currentPath))!.AsObject();
        if(working["ledPatterns"] is null)
            working["ledPatterns"]=JsonNode.Parse(File.ReadAllText(defaultsPath))!["ledPatterns"]!.DeepClone();
        Ddj200.ProfileMigration.Upgrade(working);
        Text="Configurar DDJ-200 Codex Bridge"; Width=820; Height=620; StartPosition=FormStartPosition.CenterScreen; MinimizeBox=false;
        var tabs=new TabControl { Dock=DockStyle.Fill };
        tabs.TabPages.Add(ConnectionPage()); tabs.TabPages.Add(MappingPage()); tabs.TabPages.Add(LightsPage()); tabs.TabPages.Add(JogPage());
        tabs.Selected+=(_,e)=>
        {
            if(e.TabPage?.Controls.Contains(lights)!=true)return;
            mappings.EndEdit();
            RefreshLightControls();
        };
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Bottom, Height=48, FlowDirection=FlowDirection.RightToLeft, Padding=new Padding(8) };
        var save=new Button { Text="Salvar", AutoSize=true }; save.Click+=SaveClick;
        var cancel=new Button { Text="Cancelar", AutoSize=true, DialogResult=DialogResult.Cancel };
        var restore=new Button { Text="Restaurar padrão", AutoSize=true }; restore.Click+=(_,_)=>{working=JsonNode.Parse(File.ReadAllText(defaultsPath))!.AsObject();LoadEditors();};
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(restore);
        Controls.Add(tabs); Controls.Add(buttons); AcceptButton=save; CancelButton=cancel;
        FormClosed+=(_,_)=>previewTimer.Stop();previewTimer.Tick+=(_,_)=>PreviewTick();
        LoadEditors();
    }

    private static TabPage ConnectionPage()
    {
        var page=new TabPage("Conexão");
        page.Controls.Add(new Label { Dock=DockStyle.Fill, Padding=new Padding(18), AutoSize=false, Text="Escolha USB ou Bluetooth no menu Conexão da bandeja. Apenas um transporte funciona por vez.\n\nUSB: pronto para uso após os requisitos.\nBluetooth: a escolha do DDJ acontece dentro do aplicativo; não use PIN genérico do Windows.\n\nA função de cada tecla Micro continua sendo escolhida no próprio Codex." });
        return page;
    }

    private TabPage MappingPage()
    {
        var page=new TabPage("Controles");
        mappings.Columns.Add(new DataGridViewTextBoxColumn { Name="Control",HeaderText="Controle físico",ReadOnly=true,AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill });
        var target=new DataGridViewComboBoxColumn { Name="Target",HeaderText="Função enviada ao Micro",Width=260,DisplayMember="Value",ValueMember="Key",DataSource=TargetNames.ToList() };
        mappings.Columns.Add(target); page.Controls.Add(mappings);
        var assistant=new FlowLayoutPanel { Dock=DockStyle.Top,Height=70,Padding=new Padding(8),WrapContents=false };
        assistant.Controls.Add(new Label { AutoSize=true,Margin=new Padding(3,8,8,3),Text="Função:" });
        var choice=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=220,DisplayMember="Value",ValueMember="Key",DataSource=TargetNames.ToList() };
        var learn=new Button { AutoSize=true,Text="Aperte o controle no DDJ..." };
        learn.Click+=async (_,_)=>
        {
            try
            {
                learn.Enabled=false; learn.Text="Aguardando: pressione e solte...";
                var found=await identifyControl(); if(found==null)return;
                string targetId=Convert.ToString(choice.SelectedValue)!;
                var pressed=mappings.Rows.Cast<DataGridViewRow>().SingleOrDefault(r=>(string?)r.Tag==found.Value.Control)??throw new InvalidDataException("Esse controle não pertence ao mapa seguro de 14 teclas.");
                var destination=mappings.Rows.Cast<DataGridViewRow>().Single(r=>Convert.ToString(r.Cells["Target"].Value)==targetId);
                string oldTarget=Convert.ToString(pressed.Cells["Target"].Value)!;
                destination.Cells["Target"].Value=oldTarget; pressed.Cells["Target"].Value=targetId;
                MessageBox.Show($"{ControlNames.GetValueOrDefault(found.Value.Control,found.Value.Control)} agora está associado a {TargetNames[targetId]}. As duas funções foram trocadas sem duplicar comandos.","Controle reconhecido",MessageBoxButtons.OK,MessageBoxIcon.Information);
            }
            catch(Exception ex){MessageBox.Show(ex.Message,"Não foi possível aprender",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally{learn.Enabled=true;learn.Text="Aperte o controle no DDJ...";}
        };
        assistant.Controls.Add(choice); assistant.Controls.Add(learn); page.Controls.Add(assistant);
        page.Controls.Add(new Label { Dock=DockStyle.Top,Height=44,Padding=new Padding(8),Text="Escolha uma função e pressione o controle. Durante a aprendizagem nenhum comando é enviado ao Codex." });
        return page;
    }

    private TabPage LightsPage()
    {
        var page=new TabPage("Luzes");
        lights.Columns.Add(new DataGridViewTextBoxColumn { Name="State",HeaderText="Estado",ReadOnly=true,Width=210 });
        lights.Columns.Add(new DataGridViewTextBoxColumn { Name="Controls",HeaderText="Controles (padrão compartilhado)",ReadOnly=true,Width=190 });
        lights.Columns.Add(new DataGridViewComboBoxColumn { Name="Mode",HeaderText="Padrão",Width=130,DataSource=new[]{"Aceso","Apagado","Piscar"} });
        lights.Columns.Add(new DataGridViewTextBoxColumn { Name="Timings",HeaderText="Tempos em ms: aceso, apagado, ...",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill });
        page.Controls.Add(lights);
        var tools=new FlowLayoutPanel{Dock=DockStyle.Top,Height=44,Padding=new Padding(8),WrapContents=false};
        var preview=new Button{Text="Ver exemplo",AutoSize=true};preview.Click+=(_,_)=>StartPreview();tools.Controls.Add(preview);tools.Controls.Add(lightPreview);tools.Controls.Add(new Label{AutoSize=true,Margin=new Padding(3,8,3,3),Text="Prévia somente na tela; não envia nada à DDJ."});page.Controls.Add(tools);
        page.Controls.Add(new Label { Dock=DockStyle.Top,Height=48,Padding=new Padding(8),Text="Os padrões são compartilhados pelos controles indicados abaixo. Para Piscar, use pares de tempos entre 50 e 5000 ms. Ex.: 1000, 500. Atenção e erro continuam com prioridade." });
        return page;
    }

    private void StartPreview()
    {
        try
        {
            var row=lights.CurrentRow??throw new InvalidDataException("Escolha um estado de luz.");string mode=Convert.ToString(row.Cells["Mode"].Value)??"";
            previewTimer.Stop();
            if(mode=="Aceso"){lightPreview.BackColor=Color.LimeGreen;return;}if(mode=="Apagado"){lightPreview.BackColor=Color.FromArgb(40,40,40);return;}
            previewDurations=(Convert.ToString(row.Cells["Timings"].Value)??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
            if(previewDurations.Length<2||previewDurations.Length%2!=0||previewDurations.Any(x=>x is<50 or>5000))throw new InvalidDataException("Revise os tempos antes de visualizar.");
            previewIndex=0;lightPreview.BackColor=Color.LimeGreen;previewTimer.Interval=previewDurations[0];previewTimer.Start();
        }
        catch(Exception ex)when(ex is InvalidDataException or FormatException or OverflowException){MessageBox.Show(ex.Message,"Prévia",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
    private void PreviewTick(){previewIndex=(previewIndex+1)%previewDurations.Length;lightPreview.BackColor=previewIndex%2==0?Color.LimeGreen:Color.FromArgb(40,40,40);previewTimer.Interval=previewDurations[previewIndex];}

    private TabPage JogPage()
    {
        var page=new TabPage("Jogs"); var flow=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new Padding(20),WrapContents=false };
        flow.Controls.Add(new Label { AutoSize=true,Text="Sensibilidade do jog esquerdo (padrão 0,40)" }); flow.Controls.Add(leftJog);
        flow.Controls.Add(new Label { AutoSize=true,Margin=new Padding(3,18,3,3),Text="Sensibilidade do jog direito (padrão 0,10)" }); flow.Controls.Add(rightJog);
        flow.Controls.Add(new Label { AutoSize=true,MaximumSize=new Size(700,0),Margin=new Padding(3,18,3,3),Text="O toque no topo sempre bloqueia o giro do mesmo jog e nunca reproduz movimentos descartados. O gate esquerdo permanece seguro em 50 ms." });
        page.Controls.Add(flow); return page;
    }

    private void LoadEditors()
    {
        mappings.Rows.Clear();
        foreach(var button in working["buttons"]!.AsArray())
        {
            string control=button!["control"]!.GetValue<string>(); int row=mappings.Rows.Add(ControlNames.GetValueOrDefault(control,control),button["target"]!.GetValue<string>()); mappings.Rows[row].Tag=control;
        }
        lights.Rows.Clear();
        foreach(var pair in working["ledPatterns"]!.AsObject())
        {
            var pattern=pair.Value!.AsObject(); string mode=pattern["mode"]!.GetValue<string>() switch {"solidOn"=>"Aceso","solidOff"=>"Apagado",_=>"Piscar"};
            string times=string.Join(", ",pattern["durations"]!.AsArray().Select(x=>x!.GetValue<int>()));
            lights.Rows.Add(StateNames[pair.Key],"",mode,times); lights.Rows[^1].Tag=pair.Key;
        }
        RefreshLightControls();
        leftJog.Value=(decimal)working["jog"]!["leftScale"]!.GetValue<double>(); rightJog.Value=(decimal)working["jog"]!["rightScale"]!.GetValue<double>();
    }

    private void RefreshLightControls()
    {
        string ControlsFor(bool tasks)=>string.Join(", ",mappings.Rows.Cast<DataGridViewRow>()
            .Where(r=>(Convert.ToString(r.Cells["Target"].Value)??"").StartsWith("AG",StringComparison.Ordinal)==tasks)
            .Select(r=>ControlNames.GetValueOrDefault((string)r.Tag!,(string)r.Tag!)));
        string taskControls=ControlsFor(true),commandControls=ControlsFor(false);
        foreach(DataGridViewRow row in lights.Rows)
        {
            string controls=((string)row.Tag!).StartsWith("command",StringComparison.Ordinal)?commandControls:taskControls;
            row.Cells["Controls"].Value=controls;
            row.Cells["Controls"].ToolTipText=controls;
        }
    }

    private void SaveClick(object? sender,EventArgs e)
    {
        try
        {
            var chosen=mappings.Rows.Cast<DataGridViewRow>().Select(r=>Convert.ToString(r.Cells["Target"].Value)??"").ToArray();
            if(chosen.Length!=14||chosen.Distinct().Count()!=14||!chosen.ToHashSet().SetEquals(Targets)) throw new InvalidDataException("Cada função deve aparecer exatamente uma vez.");
            var buttons=working["buttons"]!.AsArray(); for(int i=0;i<buttons.Count;i++) buttons[i]!["target"]=chosen[i];
            var led=working["ledPatterns"]!.AsObject();
            foreach(DataGridViewRow row in lights.Rows)
            {
                string key=(string)row.Tag!; string mode=Convert.ToString(row.Cells["Mode"].Value) switch {"Aceso"=>"solidOn","Apagado"=>"solidOff",_=>"blink"};
                int[] durations=mode=="blink"?(Convert.ToString(row.Cells["Timings"].Value)??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(int.Parse).ToArray():[];
                if(mode=="blink"&&(durations.Length<2||durations.Length%2!=0||durations.Any(x=>x is <50 or >5000))) throw new InvalidDataException($"Revise os tempos de {StateNames[key]}.");
                led[key]=new JsonObject{{"mode",mode},{"durations",new JsonArray(durations.Select(x=>(JsonNode?)x).ToArray())}};
            }
            working["jog"]!["leftScale"]=(double)leftJog.Value; working["jog"]!["rightScale"]=(double)rightJog.Value;
            SerializedProfile=working.ToJsonString(new JsonSerializerOptions { WriteIndented=true }); DialogResult=DialogResult.OK; Close();
        }
        catch(Exception ex) when(ex is InvalidDataException or FormatException or OverflowException) { MessageBox.Show(ex.Message,"Configuração inválida",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
    }
}
