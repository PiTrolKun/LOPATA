"""Native keyboard Save/Open/Close must use LOPATA's files and draft lifecycle."""
import unohelper
from com.sun.star.frame import XDispatchProviderInterceptor, XDispatch


class NativeCommands(unohelper.Base, XDispatchProviderInterceptor, XDispatch):
    ROUTES = {'.uno:Save': 'save', '.uno:SaveAs': 'saveAs', '.uno:Open': 'open',
              '.uno:NewDoc': 'new', '.uno:AddDirect': 'new', '.uno:CloseDoc': 'close',
              '.uno:Quit': 'close'}

    def __init__(self):
        self.master = self.slave = None
        self.pending = []

    def getMasterDispatchProvider(self): return self.master
    def setMasterDispatchProvider(self, value): self.master = value
    def getSlaveDispatchProvider(self): return self.slave
    def setSlaveDispatchProvider(self, value): self.slave = value

    def queryDispatch(self, url, target, flags):
        if url.Complete in self.ROUTES: return self
        return self.slave.queryDispatch(url, target, flags) if self.slave else None

    def queryDispatches(self, requests):
        return tuple(self.queryDispatch(r.FeatureURL, r.FrameName, r.SearchFlags) for r in requests)

    def dispatch(self, url, arguments):
        action = self.ROUTES.get(url.Complete)
        if action and action not in self.pending: self.pending.append(action)

    def addStatusListener(self, listener, url): pass
    def removeStatusListener(self, listener, url): pass

    def pop(self): return self.pending.pop(0) if self.pending else ''
