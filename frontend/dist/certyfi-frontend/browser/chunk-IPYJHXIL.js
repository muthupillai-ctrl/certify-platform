function s(t){return t.status==="Active"&&t.expiresOn&&new Date(t.expiresOn)<new Date?"Expired":t.status}export{s as a};
