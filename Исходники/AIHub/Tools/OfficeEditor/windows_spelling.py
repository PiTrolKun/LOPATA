"""Windows spell checking exposed to Writer's own underline/menu/undo pipeline.

COM objects are created and released in their calling apartment. No dictionaries
or document ranges are copied into a second editor; Writer owns those ranges.
"""
import ctypes as C
from functools import lru_cache
import uuid

P = C.c_void_p
ole = C.OleDLL('ole32')
ole.CoCreateInstance.argtypes = [P, P, C.c_uint32, P, C.POINTER(P)]
ole.CoTaskMemFree.argtypes = [P]


def guid(value):
    return (C.c_ubyte * 16).from_buffer_copy(uuid.UUID(value).bytes_le)


def invoke(obj, slot, types, *args):
    table = C.cast(obj, C.POINTER(C.POINTER(P))).contents
    result = C.WINFUNCTYPE(C.c_long, P, *types)(table[slot])(obj, *args)
    if result < 0:
        raise OSError('Windows spelling HRESULT ' + hex(result & 0xffffffff))
    return result


def release(obj):
    if obj:
        invoke(obj, 2, [])


@lru_cache(maxsize=4096)
def check(word, language):
    try:
        initialized = ole.CoInitializeEx(None, 0) >= 0
    except OSError as error:
        # UNO may call from an already initialized STA. Use that apartment;
        # balance CoUninitialize only for our own successful initialization.
        if error.winerror != -2147417850:
            raise
        initialized = False
    factory, checker, errors, error, suggestions = P(), P(), P(), P(), P()
    try:
        ole.CoCreateInstance(guid('7ab36653-1796-484b-bdfa-e74f1db7c1dc'), None, 1,
                             guid('8e018a9d-2415-4677-bf08-794ea61f94bb'), C.byref(factory))
        supported = C.c_int()
        invoke(factory, 4, [C.c_wchar_p, C.POINTER(C.c_int)], language, C.byref(supported))
        if not supported.value:
            return None
        if not word:
            return True, ()
        invoke(factory, 5, [C.c_wchar_p, C.POINTER(P)], language, C.byref(checker))
        invoke(checker, 4, [C.c_wchar_p, C.POINTER(P)], word, C.byref(errors))
        invoke(errors, 3, [C.POINTER(P)], C.byref(error))
        if not error:
            return True, ()
        invoke(checker, 5, [C.c_wchar_p, C.POINTER(P)], word, C.byref(suggestions))
        values = []
        for _ in range(8):
            value, fetched = P(), C.c_ulong()
            invoke(suggestions, 3, [C.c_ulong, C.POINTER(P), C.POINTER(C.c_ulong)], 1, C.byref(value), C.byref(fetched))
            if not fetched.value:
                break
            try:
                values.append(C.wstring_at(value))
            finally:
                ole.CoTaskMemFree(value)
        return False, tuple(values)
    finally:
        for obj in (suggestions, error, errors, checker, factory):
            release(obj)
        if initialized:
            ole.CoUninitialize()


def install(context):
    import unohelper
    from com.sun.star.lang import Locale, XServiceInfo, XSingleComponentFactory, XInitialization, XComponent, XServiceDisplayName
    from com.sun.star.linguistic2 import XSpellChecker, XSpellAlternatives, XLinguServiceEventBroadcaster
    name = 'org.lopata.office.WindowsSpellChecker'
    service = 'com.sun.star.linguistic2.SpellChecker'
    locales = tuple(Locale(*tag.split('-'), '') for tag in ('ru-RU', 'en-US') if check('', tag) is not None)

    class Alternatives(unohelper.Base, XSpellAlternatives):
        def __init__(self, word, locale, values): self.word, self.locale, self.values = word, locale, values
        def getWord(self): return self.word
        def getLocale(self): return self.locale
        def getFailureType(self): return 1
        def getAlternativesCount(self): return len(self.values)
        def getAlternatives(self): return self.values

    class Checker(unohelper.Base, XSpellChecker, XServiceInfo, XInitialization, XComponent, XServiceDisplayName, XLinguServiceEventBroadcaster):
        def getImplementationName(self): return name
        def supportsService(self, value): return value == service
        def getSupportedServiceNames(self): return (service,)
        def getLocales(self): return locales
        def hasLocale(self, value): return any(x.Language == value.Language and x.Country == value.Country for x in locales)
        def result(self, word, locale):
            tag = 'en-US' if word.isascii() and any(c.isalpha() for c in word) else locale.Language + '-' + locale.Country
            return check(word, tag)
        def isValid(self, word, locale, props):
            result = self.result(word, locale)
            return result is None or result[0]
        def spell(self, word, locale, props):
            result = self.result(word, locale)
            return None if result is None or result[0] else Alternatives(word, locale, result[1])
        def initialize(self, args): pass
        def dispose(self): check.cache_clear()
        def addEventListener(self, listener): pass
        def removeEventListener(self, listener): pass
        def getServiceDisplayName(self, locale): return 'LOPATA · Windows spelling'
        def addLinguServiceEventListener(self, listener): return True
        def removeLinguServiceEventListener(self, listener): return True

    checker = Checker()

    class Factory(unohelper.Base, XSingleComponentFactory, XServiceInfo):
        def getImplementationName(self): return name
        def supportsService(self, value): return value == service
        def getSupportedServiceNames(self): return (service,)
        def createInstanceWithContext(self, ctx): return checker
        def createInstanceWithArgumentsAndContext(self, args, ctx): return checker

    factory = Factory()
    context.ServiceManager.insert(factory)
    manager = context.ServiceManager.createInstanceWithContext('com.sun.star.linguistic2.LinguServiceManager', context)
    for locale in locales:
        manager.setConfiguredServices(service, locale, (name,))
    return factory, checker, manager, [x.Language + '-' + x.Country for x in locales]
