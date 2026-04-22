using System;
using ChatCasino.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace ChatCasino.Windows;

public sealed class MainWindow : Window
{
    private readonly DynamicWindowRenderer renderer;

    public bool DealerView { get; set; } = true;
    public string? LocalPlayerName { get; set; }

    public MainWindow(DynamicWindowRenderer renderer)
        : base("Chat Casino - Dealer View###ChatCasinoMain")
    {
        this.renderer = renderer;
        Size = new Vector2(900, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        try
        {
            renderer.DrawContents(DealerView, LocalPlayerName);
        }
        catch
        {
            // suppress to avoid crashing entire plugin draw loop
        }
    }
}
