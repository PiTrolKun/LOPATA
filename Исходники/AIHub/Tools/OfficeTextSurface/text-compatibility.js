// The source adapter requests this check only for lossy text formats. A change to the
// live OOXML tree is compared with the imported document, so Undo is handled naturally.
const plainInline = new Set(['rStyle','b','bCs','i','iCs','color','u','highlight','sz','szCs','rFonts','strike','vertAlign','shd','lang']);
const markdownInline = new Set(['b','bCs','i','iCs','strike']);
const runSwitches = new Set(['b','bCs','i','iCs','strike']);
const paragraphProperties = new Set(['pStyle','pBdr','shd','ind','numPr','jc','spacing','tabs','pageBreakBefore','keepLines','keepNext','bidi']);
const sectionProperties = new Set(['sectPr']);
const resources = new Set(['drawing','pict','object','tbl','hyperlink','commentReference','ins','del','footnoteReference','endnoteReference',
  'fldChar','fldSimple','sdt','oMath','oMathPara','altChunk','customXml','lastRenderedPageBreak']);
function shape(node) {
  const attrs=(node.attributes || []).map(a=>[a.namespaceUri,a.localName,a.value])
    .filter(([,name])=>!name?.startsWith('rsid')).sort((a,b)=>JSON.stringify(a).localeCompare(JSON.stringify(b)));
  return [node.localName,attrs,(node.children || []).filter(c=>c.kind!=='textValue').map(shape)];
}
function featureSignatures(root,markdown) {
  const properties=new Set(), counted=new Map();
  function visit(node,parent) {
    const name=node.localName;
    if(!name)return;
    if(parent==='rPr' && plainInline.has(name)) {
      // Word can explicitly switch off the last character format. For a TXT
      // source this restores plain text, including after a checkpoint/reopen.
      // Markdown retains its source style/context in the independent comparison.
      const value=node.attributes?.find(a=>a.localName==='val')?.value?.toLowerCase();
      if(!markdown && ((runSwitches.has(name) && ['0','false','off'].includes(value)) ||
        (name==='u' && value==='none')))return;
      if(!markdown || !markdownInline.has(name))properties.add(JSON.stringify(shape(node)));
      return;
    }
    if(parent==='pPr' && paragraphProperties.has(name)) {
      const value=node.attributes?.find(a=>a.localName==='val')?.value || '';
      if(!(markdown && ((name==='pStyle' && /^heading[1-6]$/i.test(value)) || name==='numPr')))
        properties.add(JSON.stringify(shape(node)));
      return;
    }
    if(sectionProperties.has(name))properties.add(JSON.stringify(shape(node)));
    if(resources.has(name) && name!=='lastRenderedPageBreak') {
      if(!(markdown && name==='hyperlink'))counted.set(name,(counted.get(name)||0)+1);
    }
    if(name==='br' && node.attributes.some(a=>a.localName==='type'&&a.value==='page')) counted.set('pageBreak',(counted.get('pageBreak')||0)+1);
    for(const child of node.children || [])visit(child,name);
  }
  visit(root,'');
  return {properties,counted};
}
const categories = {
  rFonts:'font',sz:'font',szCs:'font',color:'font',highlight:'font',shd:'font',
  rStyle:'emphasis',b:'emphasis',bCs:'emphasis',i:'emphasis',iCs:'emphasis',u:'emphasis',
  strike:'emphasis',vertAlign:'emphasis',lang:'emphasis',
  pStyle:'paragraph',pBdr:'paragraph',ind:'paragraph',jc:'paragraph',spacing:'paragraph',
  tabs:'paragraph',keepLines:'paragraph',keepNext:'paragraph',bidi:'paragraph',
  numPr:'list',tbl:'table',drawing:'image',pict:'image',hyperlink:'link',
  commentReference:'comment',pageBreakBefore:'page',sectPr:'page',pageBreak:'page',
};
function lossReasons(source,edited) {
  const reasons=new Set();
  for(const signature of edited.properties) {
    if(source.properties.has(signature))continue;
    const [element]=JSON.parse(signature);
    reasons.add(categories[element] || 'object');
  }
  for(const [element,count] of edited.counted) {
    if(count>(source.counted.get(element)||0))reasons.add(categories[element] || 'object');
  }
  return [...reasons].sort();
}
export function textCompatibility(root) {
  return {plain:featureSignatures(root,false),markdown:featureSignatures(root,true)};
}
export function compareCompatibility(source,root) {
  const edited=textCompatibility(root);
  const plainReasons=lossReasons(source.plain,edited.plain);
  const markdownReasons=lossReasons(source.markdown,edited.markdown);
  return {plainCompatible:plainReasons.length===0,markdownCompatible:markdownReasons.length===0,
    plainReasons,markdownReasons};
}

// A crash-recovered working DOCX already contains edits. Compare it with the
// original import's feature set, saved in the private draft alongside the project.
export function serializeCompatibilityBaseline(baseline) {
  function serialize(features) {
    return {properties:[...features.properties].sort(), counted:[...features.counted].sort()};
  }
  return JSON.stringify({version:1, plain:serialize(baseline.plain), markdown:serialize(baseline.markdown)});
}
export function restoreCompatibilityBaseline(encoded) {
  const invalid = () => { throw new Error('OfficeEditor.DraftFailed'); };
  if(typeof encoded !== 'string' || encoded.length > 2*1024*1024)invalid();
  let data;
  try { data=JSON.parse(encoded); } catch { invalid(); }
  if(data?.version!==1)invalid();
  function restore(features) {
    if(!Array.isArray(features?.properties) || !Array.isArray(features?.counted))invalid();
    if(!features.properties.every(property=>typeof property==='string'))invalid();
    if(!features.counted.every(item=>Array.isArray(item) && item.length===2 && typeof item[0]==='string' &&
      Number.isSafeInteger(item[1]) && item[1]>=0))invalid();
    return {properties:new Set(features.properties),counted:new Map(features.counted)};
  }
  return {plain:restore(data.plain),markdown:restore(data.markdown)};
}
