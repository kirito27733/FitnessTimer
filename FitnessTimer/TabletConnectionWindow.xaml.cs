using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using FitnessTimer.Core;

namespace FitnessTimer;

public partial class TabletConnectionWindow : Window
{
    private readonly TabletTestServer server;
    private readonly Action refreshSnapshot;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(1)};
    public TabletConnectionWindow(TabletTestServer server,Action refreshSnapshot)
    {
        InitializeComponent();this.server=server;this.refreshSnapshot=refreshSnapshot;
        if(server.IsRunning)PortBox.Text=server.Port.ToString();
        timer.Tick+=(_,_)=>RefreshState();timer.Start();Closed+=(_,_)=>timer.Stop();RefreshState();RefreshAddresses();
    }
    private void RefreshState()
    {
        ServiceStateText.Text=server.IsRunning?$"● 服务已启动 · 1号赛道 · 端口 {server.Port}":"● 服务已停止";
        ClientStateText.Text=server.ClientStatus;
        PortBox.IsEnabled=StartServiceButton.IsEnabled=!server.IsRunning;
        StopServiceButton.IsEnabled=LocalTestButton.IsEnabled=server.IsRunning;
        CopyAddressButton.IsEnabled=server.IsRunning&&AddressBox.Items.Count>0;
    }
    private void RefreshAddresses()
    {
        var selected=AddressBox.SelectedItem as string;
        AddressBox.ItemsSource=server.TabletUrls;AddressBox.SelectedItem=selected;
        if(AddressBox.SelectedIndex<0&&AddressBox.Items.Count>0)AddressBox.SelectedIndex=0;
        RefreshState();
    }
    private void Start_Click(object sender,RoutedEventArgs e)
    {
        if(!int.TryParse(PortBox.Text,out var port)||port<1024||port>65535){MessageBox.Show(this,"请输入1024至65535之间的端口号。","平板服务");return;}
        try{refreshSnapshot();server.Start(port);RefreshAddresses();refreshSnapshot();}
        catch(Exception ex){MessageBox.Show(this,$"服务未启动，请检查端口是否被占用，或更换端口。\n{ex.Message}","平板服务");RefreshState();}
    }
    private void Stop_Click(object sender,RoutedEventArgs e){server.Stop();RefreshAddresses();refreshSnapshot();}
    private void Refresh_Click(object sender,RoutedEventArgs e)=>RefreshAddresses();
    private void Copy_Click(object sender,RoutedEventArgs e)
    {if(AddressBox.SelectedItem is string address)try{Clipboard.SetText(address);}catch(Exception ex){MessageBox.Show(this,ex.Message,"复制地址失败");}}
    private void LocalTest_Click(object sender,RoutedEventArgs e)
    {if(server.IsRunning)try{Process.Start(new ProcessStartInfo(server.LocalUrl){UseShellExecute=true});}catch(Exception ex){MessageBox.Show(this,ex.Message,"打开测试页失败");}}
}
