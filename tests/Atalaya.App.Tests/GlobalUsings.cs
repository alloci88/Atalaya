// F14: los contratos del auditor (payloads, toolboxes, IAuditorProvider, readiness) viven en
// Atalaya.Agents desde que hay dos proveedores. Se declara global para que los ficheros que ya
// decian `using Atalaya.Copilot;` los sigan viendo sin una linea nueva en cada uno: lo que se
// movio fue el ensamblado, no el vocabulario.
global using Atalaya.Agents;
