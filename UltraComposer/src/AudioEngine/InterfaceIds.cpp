// This file defines the storage for every VST3 interface ID used
// anywhere in this project (Steinberg::Vst::IComponent::iid and friends).
// It must be exactly one .cpp in the whole link that does this - see the
// comment on INIT_CLASS_IID in thirdparty/vst3sdk/pluginterfaces/base/funknown.h.
// Every other .cpp just declares/uses these IDs; only here do they get
// real storage, so nothing else may also define INIT_CLASS_IID.
#define INIT_CLASS_IID

// Some of the headers this file includes (to reach every interface's IID)
// call plain strcpy/sscanf/etc. in inline code; see the identical comment in
// thirdparty/vst3sdk/pluginterfaces/base/funknown.cpp for why this is here.
#define _CRT_SECURE_NO_WARNINGS

#include "thirdparty/vst3sdk/pluginterfaces/base/funknown.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/funknownimpl.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/ibstream.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/icloneable.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/ierrorcontext.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/ipersistent.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/ipluginbase.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/iplugincompatibility.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/istringresult.h"
#include "thirdparty/vst3sdk/pluginterfaces/base/iupdatehandler.h"
#include "thirdparty/vst3sdk/pluginterfaces/gui/iplugview.h"
#include "thirdparty/vst3sdk/pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "thirdparty/vst3sdk/pluginterfaces/gui/iwaylandframe.h"
#include "thirdparty/vst3sdk/pluginterfaces/test/itest.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstattributes.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstaudioprocessor.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstautomationstate.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstchannelcontextinfo.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstcomponent.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstcontextmenu.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstdataexchange.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivsteditcontroller.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstevents.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivsthostapplication.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstinterappaudio.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstmessage.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstmidilearn.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstmidimapping2.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstnoteexpression.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstnoteonorchestralarticulationinfo.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstparameterchanges.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstparameterfunctionname.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstphysicalui.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstpluginterfacesupport.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstplugview.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstprefetchablesupport.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstremapparamid.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstrepresentation.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivsttestplugprovider.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivsttransportcontrol.h"
#include "thirdparty/vst3sdk/pluginterfaces/vst/ivstunits.h"
