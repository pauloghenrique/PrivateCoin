import { build } from 'esbuild';
import { mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const directory = path.dirname(fileURLToPath(import.meta.url));
const site = path.resolve(directory, '..');
const dist = path.join(directory, 'dist');
await mkdir(dist, { recursive: true });
await build({ entryPoints: [path.join(directory, 'src/app.mjs')], bundle: true, format: 'iife', platform: 'browser', target: 'es2022', minify: true, legalComments: 'eof', outfile: path.join(site, 'Scripts/povix-swap.js'), banner: { js: '/* Generated from PrivateCoin.Site/Swap/src. Run npm run build in Swap to update. */' } });
const markup = await readFile(path.join(directory, 'markup.html'), 'utf8');
const view = '@{\n    ViewBag.Title = "POVIX Swap";\n}\n<link rel="stylesheet" href="@Url.Content("~/Content/povix-swap.css")" />\n' + markup.replace('data-execution-enabled="false"', 'data-execution-enabled="@(ViewBag.SwapExecutionEnabled == true ? "true" : "false")"') + '\n@section scripts {\n    <script src="@Url.Content("~/Scripts/povix-swap.js")" defer></script>\n}\n';
await writeFile(path.join(site, 'Views/Home/Swap.cshtml'), view);
await copyFile(path.join(site, 'Content/povix-swap.css'), path.join(dist, 'styles.css'));
await copyFile(path.join(site, 'Scripts/povix-swap.js'), path.join(dist, 'swap.js'));
await writeFile(path.join(dist, 'index.html'), '<!doctype html>\n<html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>POVIX Swap</title><meta name="description" content="Prévia da POVIX Swap: conexão de carteira e cotações entre Bitcoin, Ethereum e BNB."><link rel="stylesheet" href="styles.css"><style>body{margin:0;background:#f5f9f6}button,input,select{font:inherit}</style></head><body>' + markup + '<script src="swap.js" defer></script></body></html>');
console.log('Built ASP.NET view, browser bundle, and standalone preview. Real broadcasts default to disabled.');
