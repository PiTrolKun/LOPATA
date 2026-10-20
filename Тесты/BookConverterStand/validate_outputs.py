"""Independent schema validators already prepared in this stand. No downloads or source changes."""
from pathlib import Path
import json,shutil,subprocess,sys

STAND=Path(__file__).resolve().parent
sys.path.insert(0,str(STAND/'validators/python'))
from lxml import etree

def main():
 folder=Path(sys.argv[1]).resolve()
 if not folder.is_relative_to(STAND):raise ValueError('Validation input must belong to the test stand')
 label=folder.name
 java=STAND/'validators/temurin/jdk-21.0.12.1+1-jre/bin/java.exe'
 jar=STAND/'validators/epubcheck/epubcheck-5.4.0/epubcheck.jar'
 results=[]
 for extension in ['epub','kepub']:
  for source in sorted((folder/extension).glob('*.'+extension)):
   validate=source
   if extension=='kepub':
    validate=STAND/(label+'-kepub-validation.epub');shutil.copyfile(source,validate)
   report=STAND/('epubcheck-'+label+'-'+extension+'.json')
   p=subprocess.run([str(java),'-jar',str(jar),str(validate),'--json',str(report),'--maxOfEachMessage','unlimited'],capture_output=True,timeout=180)
   results.append({'file':str(source.relative_to(STAND)),'validator':'EPUBCheck5.4.0','exitCode':p.returncode,'passed':p.returncode==0,'report':report.name})
 parser=etree.XMLParser(resolve_entities=False,no_network=True)
 schema=etree.XMLSchema(etree.parse(str(STAND/'schema/FictionBook.xsd'),parser))
 for source in sorted((folder/'fb2').glob('*.fb2')):
  tree=etree.parse(str(source),parser)
  passed=schema.validate(tree)
  results.append({'file':str(source.relative_to(STAND)),'validator':'lxml6.0.2 officialFB2XSD','passed':passed,'errors':[str(e) for e in schema.error_log]})
 result={'input':label,'allPassed':len(results)==3 and all(x['passed'] for x in results),'results':results}
 report=STAND/('independent-validation-'+label+'.json')
 report.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
 print(json.dumps(result,ensure_ascii=False,indent=2))
 raise SystemExit(0 if result['allPassed'] else 1)

if __name__=='__main__':main()
