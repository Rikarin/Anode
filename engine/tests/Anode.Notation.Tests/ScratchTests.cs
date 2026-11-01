using Anode.Contracts;
using static Anode.Notation.Tests.TestProject;

namespace Anode.Notation.Tests;

public sealed class ScratchTests {
    [Fact]
    public void Print() {
        var p = new TestProject();
        var t = p.Track("Piano");
        p.Region(
            t,
            0,
            3840 * 3,
            Grid(240, 160),
            N(1, 60, 12, 940),
            N(2, 64, 12, 940),
            N(3, 67, 12, 940),
            N(4, 62, 965, 300),
            N(5, 64, 1290, 300),
            N(6, 65, 1595, 300),
            N(7, 67, 1920, 2880),
            N(8, 48, 0, 3840),
            N(9, 76, 4800, 2880),
            N(10, 72, 4800, 960),
            N(11, 74, 5760, 960),
            N(12, 71, 6720, 960),
            N(13, 41, 3850, 1900),
            N(14, 43, 5770, 1900),
            N(15, 60, 7680, 3840),
            N(16, 64, 7680, 3840),
            N(17, 67, 7680, 3840),
            N(18, 48, 7680, 3840)
        );
        var view = NotationEngine.Render(p.State, ProjectScore.Empty, new());
        var dir = "/private/tmp/claude-501/-Users-jiu-Projects-Anode/5917b924-6716-4ee3-9e3b-154f801dc07a/scratchpad/ws9A";
        File.WriteAllText(Path.Combine(dir, "view.txt"), Describe.View(view));
        File.WriteAllText(Path.Combine(dir, "view.mei"), MeiWriter.Write(view));
    }
}
