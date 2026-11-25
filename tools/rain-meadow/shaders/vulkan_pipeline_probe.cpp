#include <vulkan/vulkan.h>
#include <fstream>
#include <iostream>
#include <vector>
#include <map>
#include <string>
#include <stdexcept>
#include <dlfcn.h>

static void check(VkResult result, const char* where) {
    if (result != VK_SUCCESS) throw std::runtime_error(std::string(where)+": "+std::to_string(result));
}
static std::vector<uint32_t> read(const char* path) {
    std::ifstream file(path,std::ios::binary|std::ios::ate);
    if (!file || file.tellg()<=0 || file.tellg()%4) throw std::runtime_error("Invalid SPIR-V file");
    std::vector<uint32_t> words(size_t(file.tellg())/4);file.seekg(0);file.read((char*)words.data(),words.size()*4);return words;
}
int main(int argc,char** argv) {
    if(argc!=4)return 2;
    VkInstance instance=VK_NULL_HANDLE;VkDevice device=VK_NULL_HANDLE;
    try {
        Dl_info loader{};dladdr((void*)vkCreateInstance,&loader);std::cout<<"VULKAN_LOADER "<<(loader.dli_fname?loader.dli_fname:"unknown")<<std::endl;
        VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};app.pApplicationName="shader-conversion-probe";app.apiVersion=VK_API_VERSION_1_0;
        VkInstanceCreateInfo info{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};info.pApplicationInfo=&app;
        check(vkCreateInstance(&info,nullptr,&instance),"vkCreateInstance");
        uint32_t count=0;check(vkEnumeratePhysicalDevices(instance,&count,nullptr),"devices");
        if(!count)throw std::runtime_error("No Vulkan device");
        std::vector<VkPhysicalDevice> physical(count);check(vkEnumeratePhysicalDevices(instance,&count,physical.data()),"devices");
        auto gpu=physical.front();VkPhysicalDeviceProperties properties;vkGetPhysicalDeviceProperties(gpu,&properties);
        std::cout<<"VULKAN_DEVICE "<<properties.deviceName<<" api="<<properties.apiVersion<<std::endl;
        vkGetPhysicalDeviceQueueFamilyProperties(gpu,&count,nullptr);std::vector<VkQueueFamilyProperties> families(count);vkGetPhysicalDeviceQueueFamilyProperties(gpu,&count,families.data());
        uint32_t queue=0;while(queue<count&&!(families[queue].queueFlags&VK_QUEUE_GRAPHICS_BIT))++queue;
        if(queue==count)throw std::runtime_error("No graphics queue");
        float priority=1;VkDeviceQueueCreateInfo qi{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};qi.queueFamilyIndex=queue;qi.queueCount=1;qi.pQueuePriorities=&priority;
        VkPhysicalDeviceFeatures features;vkGetPhysicalDeviceFeatures(gpu,&features);
        VkDeviceCreateInfo di{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};di.queueCreateInfoCount=1;di.pQueueCreateInfos=&qi;di.pEnabledFeatures=&features;
        check(vkCreateDevice(gpu,&di,nullptr,&device),"vkCreateDevice");
        std::vector<std::vector<VkDescriptorSetLayoutBinding>> bindings(2);
        std::vector<VkVertexInputAttributeDescription> attributes;
        std::ifstream plan(argv[3]);std::string kind;
        while(plan>>kind) {
            uint32_t set,binding,stage,size;
            if(kind=="input") {
                plan>>binding>>size;
                VkFormat format=size==4?VK_FORMAT_R32G32B32A32_SFLOAT:size==3?VK_FORMAT_R32G32B32_SFLOAT:size==2?VK_FORMAT_R32G32_SFLOAT:VK_FORMAT_R32_SFLOAT;
                attributes.push_back({binding,0,format,binding*16});
            } else {
                plan>>set>>binding>>stage>>size;
                if(set>=bindings.size())throw std::runtime_error("Unsupported descriptor set");
                VkDescriptorType type=kind=="cb"?VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER:kind=="t"?VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE:VK_DESCRIPTOR_TYPE_SAMPLER;
                bindings[set].push_back({binding,type,1,stage,nullptr});
            }
        }
        VkDescriptorSetLayout layouts[2];
        for(int i=0;i<2;++i) {
            VkDescriptorSetLayoutCreateInfo l{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};l.bindingCount=bindings[i].size();l.pBindings=bindings[i].data();
            check(vkCreateDescriptorSetLayout(device,&l,nullptr,&layouts[i]),"descriptor layout");
        }
        VkPipelineLayoutCreateInfo li{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};li.setLayoutCount=2;li.pSetLayouts=layouts;
        VkPipelineLayout layout;check(vkCreatePipelineLayout(device,&li,nullptr,&layout),"pipeline layout");
        VkShaderModule modules[2];VkPipelineShaderStageCreateInfo stages[2];
        for(int i=0;i<2;++i) {
            auto words=read(argv[i+1]);VkShaderModuleCreateInfo mi{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};mi.codeSize=words.size()*4;mi.pCode=words.data();
            check(vkCreateShaderModule(device,&mi,nullptr,&modules[i]),"shader module");
            stages[i]={VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO};stages[i].stage=i?VK_SHADER_STAGE_FRAGMENT_BIT:VK_SHADER_STAGE_VERTEX_BIT;stages[i].module=modules[i];stages[i].pName="main";
        }
        VkAttachmentDescription attachment{};attachment.format=VK_FORMAT_R8G8B8A8_UNORM;attachment.samples=VK_SAMPLE_COUNT_1_BIT;attachment.loadOp=VK_ATTACHMENT_LOAD_OP_CLEAR;attachment.storeOp=VK_ATTACHMENT_STORE_OP_STORE;attachment.initialLayout=VK_IMAGE_LAYOUT_UNDEFINED;attachment.finalLayout=VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
        VkAttachmentReference color{0,VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};VkSubpassDescription subpass{};subpass.pipelineBindPoint=VK_PIPELINE_BIND_POINT_GRAPHICS;subpass.colorAttachmentCount=1;subpass.pColorAttachments=&color;
        VkRenderPassCreateInfo ri{VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO};ri.attachmentCount=1;ri.pAttachments=&attachment;ri.subpassCount=1;ri.pSubpasses=&subpass;
        VkRenderPass renderpass;check(vkCreateRenderPass(device,&ri,nullptr,&renderpass),"render pass");
        VkVertexInputBindingDescription vb{0,128,VK_VERTEX_INPUT_RATE_VERTEX};
        VkPipelineVertexInputStateCreateInfo vi{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO};vi.vertexBindingDescriptionCount=1;vi.pVertexBindingDescriptions=&vb;vi.vertexAttributeDescriptionCount=attributes.size();vi.pVertexAttributeDescriptions=attributes.data();
        VkPipelineInputAssemblyStateCreateInfo ia{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};ia.topology=VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
        VkViewport viewport{0,0,4,4,0,1};VkRect2D scissor{{0,0},{4,4}};VkPipelineViewportStateCreateInfo vp{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO};vp.viewportCount=1;vp.pViewports=&viewport;vp.scissorCount=1;vp.pScissors=&scissor;
        VkPipelineRasterizationStateCreateInfo rs{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};rs.polygonMode=VK_POLYGON_MODE_FILL;rs.cullMode=VK_CULL_MODE_NONE;rs.lineWidth=1;
        VkPipelineMultisampleStateCreateInfo ms{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};ms.rasterizationSamples=VK_SAMPLE_COUNT_1_BIT;
        VkPipelineColorBlendAttachmentState blend{};blend.colorWriteMask=15;
        VkPipelineColorBlendStateCreateInfo bs{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO};bs.attachmentCount=1;bs.pAttachments=&blend;
        VkGraphicsPipelineCreateInfo pi{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};pi.stageCount=2;pi.pStages=stages;pi.pVertexInputState=&vi;pi.pInputAssemblyState=&ia;pi.pViewportState=&vp;pi.pRasterizationState=&rs;pi.pMultisampleState=&ms;pi.pColorBlendState=&bs;pi.layout=layout;pi.renderPass=renderpass;
        VkPipeline pipeline;check(vkCreateGraphicsPipelines(device,VK_NULL_HANDLE,1,&pi,nullptr,&pipeline),"vkCreateGraphicsPipelines");
        vkDestroyPipeline(device,pipeline,nullptr);vkDestroyRenderPass(device,renderpass,nullptr);
        for(auto m:modules)vkDestroyShaderModule(device,m,nullptr);
        vkDestroyPipelineLayout(device,layout,nullptr);for(auto l:layouts)vkDestroyDescriptorSetLayout(device,l,nullptr);
        vkDestroyDevice(device,nullptr);device=VK_NULL_HANDLE;vkDestroyInstance(instance,nullptr);instance=VK_NULL_HANDLE;
        std::cout<<"PASS Vulkan graphics pipeline created; no draw submitted; Unity not executed"<<std::endl;return 0;
    } catch(const std::exception& error) {
        std::cerr<<error.what()<<std::endl;
        if(device)vkDestroyDevice(device,nullptr);if(instance)vkDestroyInstance(instance,nullptr);return 1;
    }
}
