from pathlib import Path
root=Path(__file__).resolve().parent.parent
objects={}; n=0

def add(body):
    global n
    n+=1; key=f'{n:024X}'; objects[key]=body; return key

def arr(xs): return '('+','.join(xs)+',)'
refs={}
for p in ['Sources/BloomLanguage.swift','Sources/LidSensor.swift','Sources/BloomFrames.swift','Sources/BloomRenderer.swift','Sources/BloomApp.swift','Sources/BloomSaver.swift','Resources/BloomPoster.jpg','Resources/BloomOriginal.mp4','Resources/Bloom.metal','Resources/AppIcon.icns','Resources/AppInfo.plist','Resources/SaverInfo.plist']:
    t='sourcecode.swift' if p.endswith('.swift') else 'image.jpeg' if p.endswith('.jpg') else 'video.mp4' if p.endswith('.mp4') else 'text'
    refs[p]=add(f'isa = PBXFileReference; explicitFileType = {t}; path = "{p}"; sourceTree = "<group>";')
products=[]; targets=[]
for kind in ['Saver','App']:
    is_app=kind=='App'; name='Bloom Native' if is_app else 'Bloom Native Saver'; executable='BloomNative' if is_app else 'BloomSaver'
    product=add(f'isa = PBXFileReference; explicitFileType = {"wrapper.application" if is_app else "wrapper.cfbundle"}; path = "Bloom Native.{"app" if is_app else "saver"}"; sourceTree = BUILT_PRODUCTS_DIR;')
    products.append(product)
    files=['Sources/BloomLanguage.swift','Sources/LidSensor.swift','Sources/BloomFrames.swift','Sources/BloomRenderer.swift','Sources/BloomApp.swift'] if is_app else ['Sources/BloomSaver.swift']
    sourcebuild=[add(f'isa = PBXBuildFile; fileRef = {refs[p]};') for p in files]
    resourcebuild=[add(f'isa = PBXBuildFile; fileRef = {refs[p]};') for p in (['Resources/BloomPoster.jpg','Resources/BloomOriginal.mp4','Resources/Bloom.metal','Resources/AppIcon.icns'] if is_app else ['Resources/BloomPoster.jpg'])]
    phases=[add(f'isa = PBXSourcesBuildPhase; buildActionMask = 2147483647; files = {arr(sourcebuild)}; runOnlyForDeploymentPostprocessing = 0;'),add(f'isa = PBXResourcesBuildPhase; buildActionMask = 2147483647; files = {arr(resourcebuild)}; runOnlyForDeploymentPostprocessing = 0;')]
    if is_app:
        embed_script = 'mkdir -p "${TARGET_BUILD_DIR}/${UNLOCALIZED_RESOURCES_FOLDER_PATH}"\n/usr/bin/ditto "${BUILT_PRODUCTS_DIR}/Bloom Native.saver" "${TARGET_BUILD_DIR}/${UNLOCALIZED_RESOURCES_FOLDER_PATH}/Bloom Native.saver"'
        import json
        phases.append(add('isa = PBXShellScriptBuildPhase; buildActionMask = 2147483647; files = (); inputPaths = (); outputPaths = (); runOnlyForDeploymentPostprocessing = 0; shellPath = /bin/sh; shellScript = '+json.dumps(embed_script)+';'))
    configs=[]
    for config in ['Debug','Release']:
        settings={'ARCHS':'arm64','SDKROOT':'macosx','MACOSX_DEPLOYMENT_TARGET':'14.0','SWIFT_VERSION':'5.0','SWIFT_OPTIMIZATION_LEVEL':'-Onone' if config=='Debug' else '-O','CODE_SIGN_IDENTITY':'-','CODE_SIGN_STYLE':'Manual','PRODUCT_NAME':'Bloom Native','EXECUTABLE_NAME':executable,'PRODUCT_MODULE_NAME':executable,'ALWAYS_SEARCH_USER_PATHS':'NO','INFOPLIST_FILE':f'Resources/{kind}Info.plist','PRODUCT_BUNDLE_IDENTIFIER':'local.bloom.native'+('' if is_app else '.saver'),'ENABLE_APP_SANDBOX':'NO'}
        if not is_app: settings.update(WRAPPER_EXTENSION='saver',MACH_O_TYPE='mh_bundle')
        configs.append(add('isa = XCBuildConfiguration; name = '+config+'; buildSettings = {'+''.join(f'{k} = "{v}";' for k,v in settings.items())+'};'))
    configlist=add(f'isa = XCConfigurationList; buildConfigurations = {arr(configs)}; defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
    dependencies=[]
    if is_app:
        dependencies=[add(f'isa = PBXTargetDependency; target = {targets[0]};')]
    targets.append(add(f'isa = PBXNativeTarget; name = "{name}"; productName = "Bloom Native"; buildConfigurationList = {configlist}; buildPhases = {arr(phases)}; buildRules = (); dependencies = {arr(dependencies) if dependencies else "()"}; productReference = {product}; productType = "com.apple.product-type.{"application" if is_app else "bundle"}";'))
productgroup=add(f'isa = PBXGroup; name = Products; children = {arr(products)}; sourceTree = "<group>";')
group=add(f'isa = PBXGroup; children = {arr(list(refs.values())+[productgroup])}; sourceTree = "<group>";')
configs=[add(f'isa = XCBuildConfiguration; name = {c}; buildSettings = {{}};') for c in ['Debug','Release']]
cl=add(f'isa = XCConfigurationList; buildConfigurations = {arr(configs)}; defaultConfigurationIsVisible = 0; defaultConfigurationName = Release;')
project=add(f'isa = PBXProject; buildConfigurationList = {cl}; compatibilityVersion = "Xcode 14.0"; developmentRegion = en; hasScannedForEncodings = 0; knownRegions = (en,Base,); mainGroup = {group}; productRefGroup = {productgroup}; projectDirPath = ""; projectRoot = ""; targets = {arr(targets)};')
folder=root/'BloomNative.xcodeproj'; folder.mkdir(exist_ok=True)
(folder/'project.pbxproj').write_text('// !$*UTF8*$!\n{ archiveVersion = 1; classes = {}; objectVersion = 56; objects = {\n'+ '\n'.join(k+' = { '+v+' };' for k,v in objects.items())+'\n}; rootObject = '+project+'; }\n')
