import sys, time, re
sys.path.insert(0, r'C:\Users\ben\repos\ftk2-mods-crucible\FTK2.Crucible\tools')
import drive

def active_guid():
    s = drive.run('crucible_combat_snapshot', [], strict=False)
    m = re.search(r'activeGuid=(\S+)', s)
    return (m.group(1) if m else None), s

def turn_to(guid, max_turns=14):
    for i in range(max_turns):
        cur, s = active_guid()
        if cur == guid:
            return True, s
        drive.run('crucible_combat_end_turn', [], strict=False)
        time.sleep(3)
    return False, s

if __name__ == "__main__":
    ok, s = turn_to(sys.argv[1])
    print("reached=%s" % ok)
    print(s.split('tiles=')[0])
