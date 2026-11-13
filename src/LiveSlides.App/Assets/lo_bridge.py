"""LiveSlides <-> LibreOffice bridge.

Runs inside LibreOffice's own Python. Starts a private LibreOffice instance, then obeys simple
commands from LiveSlides so that presentations are played by Impress itself (animations,
transitions and embedded media included) instead of being flattened to images.

argv: <soffice.bin> <profile dir> <tcp port, 0 = any free port>
Protocol: one TCP connection per command on 127.0.0.1:<port>; request = one line of
tab-separated fields, reply = one JSON line.
"""
import json
import pathlib
import shutil
import socket
import subprocess
import sys
import threading
import time
import traceback

import uno
import unohelper
from com.sun.star.beans import PropertyValue
from com.sun.star.awt import XCallback
from com.sun.star.presentation import XSlideShowListener

SOFFICE, PROFILE, PORT = sys.argv[1], pathlib.Path(sys.argv[2]), int(sys.argv[3])


def prop(name, value):
    p = PropertyValue()
    p.Name, p.Value = name, value
    return p


class Watcher(unohelper.Base, XSlideShowListener):
    """Told by the running slide show when the current slide has no animation step left.

    Counting clicks instead would be wrong: a click during a long animation only finishes it."""

    def __init__(self):
        self.done = False

    def slideTransitionStarted(self):
        self.done = False

    def slideAnimationsEnded(self):
        self.done = True

    def slideTransitionEnded(self): pass
    def slideEnded(self, reverse): pass
    def hyperLinkClicked(self, link): pass
    def paused(self): pass
    def resumed(self): pass
    def beginEvent(self, node): pass
    def endEvent(self, node): pass
    def repeat(self, node, count): pass
    def disposing(self, source): pass


class MainThreadCall(unohelper.Base, XCallback):
    """Runs a function on LibreOffice's own main thread (see Office.on_main)."""

    def __init__(self, function):
        self.function = function
        self.finished = threading.Event()
        self.result = None
        self.error = None

    def notify(self, data):
        try:
            self.result = self.function()
        except Exception as error:
            self.error = error
        self.finished.set()


class Office:
    def __init__(self):
        self.proc = None
        self.desktop = None
        self.docs = {}
        self.opened = {}
        self.watchers = {}
        self.next_id = 1
        self.presenter_setting = None

    def alive(self):
        return self.proc is not None and self.proc.poll() is None

    def start(self):
        # Dumps of an earlier crash make the next start show a crash-report dialog.
        shutil.rmtree(PROFILE / "crash", ignore_errors=True)
        pipe = "liveslides_%d_%d" % (PORT, int(time.time() * 1000))
        command = [SOFFICE, "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
                   f"--accept=pipe,name={pipe};urp;", f"-env:UserInstallation={PROFILE.as_uri()}"]
        self.proc = subprocess.Popen(command)
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext("com.sun.star.bridge.UnoUrlResolver", local)
        ctx = None
        deadline = time.time() + 90  # the very first start also creates the profile
        launches = 1
        while ctx is None:
            try:
                ctx = resolver.resolve(f"uno:pipe,name={pipe};urp;StarOffice.ComponentContext")
            except Exception:
                if not self.alive() and launches < 3:
                    # Normal on a brand new profile: LibreOffice sets it up and exits.
                    launches += 1
                    self.proc = subprocess.Popen(command)
                elif time.time() > deadline or not self.alive():
                    raise RuntimeError("LibreOffice non si avvia")
                time.sleep(0.2)
        smgr = ctx.ServiceManager
        self.desktop = smgr.createInstanceWithContext("com.sun.star.frame.Desktop", ctx)
        self.main_thread = smgr.createInstanceWithContext("com.sun.star.awt.AsyncCallback", ctx)
        self.docs = {}
        self.opened = {}
        # The presenter console would open on the operator's screen: LiveSlides is the console.
        try:
            provider = smgr.createInstanceWithContext("com.sun.star.configuration.ConfigurationProvider", ctx)
            access = provider.createInstanceWithArguments(
                "com.sun.star.configuration.ConfigurationUpdateAccess",
                (prop("nodepath", "/org.openoffice.Office.Impress/Misc/Start"),))
            access.setPropertyValue("EnablePresenterScreen", False)
            access.commitChanges()
            self.presenter_setting = access
        except Exception:
            self.presenter_setting = None

    def on_main(self, function, seconds=8.0):
        """Starting and ending a slide show from this script's own thread sooner or later leaves
        LibreOffice hung; done on its main thread, as when a person does it, it is reliable."""
        call = MainThreadCall(function)
        self.main_thread.addCallback(call, None)
        if not call.finished.wait(seconds):
            raise RuntimeError("LibreOffice bloccato (disposed)")
        if call.error is not None:
            raise call.error
        return call.result

    def ensure(self):
        if not self.alive():
            self.start()

    def stop(self):
        for doc in list(self.docs.values()):
            try:
                if doc.Presentation.isRunning():
                    doc.Presentation.end()
            except Exception:
                pass
        # Killed, not asked to quit: a running slide show can veto or hang a clean exit, and there
        # is nothing to save (documents are opened read-only, the profile is ours).
        if self.proc is not None:
            try:
                self.proc.kill()
                self.proc.wait(5)
            except Exception:
                pass
        self.proc = None

    # ---- commands

    def open(self, path):
        self.ensure()
        # A file already loaded is reused (never load with Hidden=True to avoid the window: with
        # several hidden documents LibreOffice hangs or drops them when a slide show ends).
        known = self.opened.get(path)
        if known is not None:
            if self.usable(known["id"]):
                return known
            self.docs.pop(known["id"], None)
            del self.opened[path]
        doc = self.desktop.loadComponentFromURL(
            uno.systemPathToFileUrl(path), "_blank", 0, (prop("ReadOnly", True), prop("OnMainThread", True)))
        if doc is None or not hasattr(doc, "Presentation"):
            raise RuntimeError("non è una presentazione")
        # The editing window must never cover the operator's screen.
        try:
            window = doc.CurrentController.Frame.ContainerWindow
            # Minimized right after loading. Do not try to avoid the brief flash of this window:
            # loading with Hidden=True, or making LibreOffice create the window already minimized,
            # ends in slide shows that hang when started a second time.
            window.IsMinimized = True
        except Exception:
            pass
        doc_id = self.next_id
        self.next_id += 1
        self.docs[doc_id] = doc
        info = {"id": doc_id, "slides": doc.DrawPages.Count,
                "clicks": [self.clicks(doc, i) for i in range(doc.DrawPages.Count)]}
        self.opened[path] = info
        return info

    def usable(self, doc_id):
        try:
            return self.docs[int(doc_id)].DrawPages.Count > 0
        except Exception:
            return False

    @staticmethod
    def clicks(doc, index):
        """Mouse clicks the slide needs before it is over (its click-triggered animation steps)."""
        try:
            count = 0
            sequences = doc.DrawPages.getByIndex(index).AnimationNode.createEnumeration()
            while sequences.hasMoreElements():
                sequence = sequences.nextElement()
                if sequence.Type != 2 or not hasattr(sequence, "createEnumeration"):  # 2 = SEQ
                    continue
                steps = sequence.createEnumeration()
                while steps.hasMoreElements():
                    steps.nextElement()
                    count += 1
            return count
        except Exception:
            return 0

    def controller(self, doc_id):
        doc = self.docs[int(doc_id)]
        pres = doc.Presentation
        return pres, (pres.getController() if pres.isRunning() else None)

    def state(self, doc_id):
        pres, ctl = self.controller(doc_id)
        if ctl is None:
            return {"running": False}
        watcher = self.watchers.get(int(doc_id))
        return {"running": True, "slide": ctl.getCurrentSlideIndex(), "count": ctl.getSlideCount(),
                "done": bool(watcher and watcher.done)}

    def show(self, doc_id, display, slide, presenter="0"):
        doc = self.docs[int(doc_id)]
        pres = doc.Presentation
        if pres.isRunning():
            self.on_main(pres.end)
            self.wait(lambda: not pres.isRunning())
        # The presenter console, only when LiveSlides has a screen for it (it moves the console
        # there). Read by Impress each time a show starts.
        if self.presenter_setting is not None:
            try:
                self.presenter_setting.setPropertyValue("EnablePresenterScreen", presenter == "1")
                self.presenter_setting.commitChanges()
            except Exception:
                pass
        pres.IsFullScreen = int(display) > 0
        if int(display) > 0:
            pres.Display = int(display)
        pres.IsAlwaysOnTop = False
        pres.IsMouseVisible = False
        pres.StartWithNavigator = False
        pres.IsEndless = False
        pres.FirstPage = doc.DrawPages.getByIndex(int(slide)).Name if int(slide) > 0 else ""
        self.on_main(pres.start)
        if not self.wait(lambda: pres.isRunning() and pres.getController() is not None):
            raise RuntimeError("la presentazione non parte")
        watcher = Watcher()
        self.watchers[int(doc_id)] = watcher
        pres.getController().addSlideShowListener(watcher)
        return self.state(doc_id)

    @staticmethod
    def wait(condition, seconds=10.0):
        deadline = time.time() + seconds
        while time.time() < deadline:
            try:
                if condition():
                    return True
            except Exception:
                pass
            time.sleep(0.03)
        return False

    def handle(self, fields):
        cmd, args = fields[0], fields[1:]
        if cmd == "ping":
            self.ensure()
            return {"pid": self.proc.pid}
        if cmd == "open":
            return self.open(args[0])
        if cmd == "show":
            return self.show(*args)
        if cmd == "state":
            return self.state(args[0])
        if cmd in ("next", "prev", "goto", "pause", "resume"):
            pres, ctl = self.controller(args[0])
            if ctl is None:
                return {"running": False}
            if cmd == "next":
                ctl.gotoNextEffect()
            elif cmd == "prev":
                ctl.gotoPreviousEffect()
            elif cmd == "goto":
                if int(args[0]) in self.watchers:
                    self.watchers[int(args[0])].done = False
                ctl.gotoSlideIndex(int(args[1]))
            elif cmd == "pause":
                ctl.pause()
            elif cmd == "resume":
                ctl.resume()
            return self.state(args[0])
        if cmd == "end":
            try:
                pres, _ = self.controller(args[0])
                if pres.isRunning():
                    self.on_main(pres.end)
                    self.wait(lambda: not pres.isRunning())
            except RuntimeError:
                raise
            except Exception:
                pass  # the document is already gone: nothing left to end
            return {}
        if cmd == "reset":
            # LibreOffice crashes when a document is closed after its slide show has run, so
            # documents are never closed one by one: at the end of the show the whole (private)
            # LibreOffice is thrown away, and a fresh one starts with the next show.
            self.stop()
            self.docs = {}
            self.opened = {}
            return {}
        if cmd == "eval":  # diagnostics only
            pres, ctl = self.controller(args[0])
            doc = self.docs[int(args[0])]
            return {"value": repr(eval(args[1], {"pres": pres, "ctl": ctl, "doc": doc, "uno": uno, "time": time, "prop": prop}))}
        raise RuntimeError("comando sconosciuto: " + cmd)


def main():
    office = Office()
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.bind(("127.0.0.1", PORT))
    server.listen(4)
    # The port actually bound: with 0 the system picks a free one, so no other program can
    # take it between the choice and the bind. LiveSlides reads it from this line.
    print("READY %d" % server.getsockname()[1], flush=True)
    while True:
        conn, _ = server.accept()
        with conn:
            try:
                data = b""
                while not data.endswith(b"\n"):
                    chunk = conn.recv(65536)
                    if not chunk:
                        break
                    data += chunk
                fields = data.decode("utf-8").rstrip("\r\n").split("\t")
                if fields[0] == "quit":
                    conn.sendall(b'{"ok":true}\n')
                    break
                try:
                    reply = office.handle(fields)
                    reply["ok"] = True
                except Exception as error:
                    if "disposed" in str(error).lower():
                        # LibreOffice died under us: make sure it is gone, the next command restarts it.
                        try:
                            office.proc.kill()
                            office.proc.wait(5)
                        except Exception:
                            pass
                    reply = {"ok": False, "error": str(error) or type(error).__name__, "alive": office.alive(),
                             "trace": traceback.format_exc(limit=3)}
                conn.sendall((json.dumps(reply) + "\n").encode("utf-8"))
            except OSError:
                pass
    office.stop()


main()
