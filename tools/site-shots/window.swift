// The godot-game scene's hold on the windowed Passenger it takes, by the process's id (game.py runs it):
//   swift window.swift id PID        the number of the process's largest window on screen, which screencapture -l takes
//   swift window.swift front PID     the process brought in front, its window over the others as it is taken
// Exits 1 while the process shows no window, 2 on a malformed command line.
import ApplicationServices
import CoreGraphics
import Foundation

let arguments = CommandLine.arguments
guard arguments.count >= 3, let pid = Int32(arguments[2]) else {
    FileHandle.standardError.write("usage: swift window.swift id PID | front PID\n".data(using: .utf8)!)
    exit(2)
}

func area(_ window: [String: Any]) -> Double {
    guard let bounds = window[kCGWindowBounds as String] as? [String: Double] else { return 0 }
    return (bounds["Width"] ?? 0) * (bounds["Height"] ?? 0)
}

switch arguments[1] {
case "id":
    let listed = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
    let own = listed.filter { ($0[kCGWindowOwnerPID as String] as? Int32) == pid && ($0[kCGWindowLayer as String] as? Int) == 0 }
    guard let largest = own.max(by: { area($0) < area($1) }), let number = largest[kCGWindowNumber as String] as? Int else { exit(1) }
    print(number)
case "front":
    let frontmost = AXUIElementSetAttributeValue(AXUIElementCreateApplication(pid), kAXFrontmostAttribute as CFString, kCFBooleanTrue)
    exit(frontmost == .success ? 0 : 1)
default:
    exit(2)
}
