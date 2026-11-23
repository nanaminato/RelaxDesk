#pragma once
#include <algorithm>
#include <cmath>

inline rd_viewport viewport_layout(int width,int height,int video_width,int video_height,int zoom,double pan_x,double pan_y){
    if(width<1||width>8192||height<1||height>8192||video_width<1||video_width>65536||video_height<1||video_height>65536||static_cast<int64_t>(video_width)*video_height>8294400||
        (zoom!=0&&(zoom<25||zoom>400))||!std::isfinite(pan_x)||!std::isfinite(pan_y)||pan_x<0||pan_x>1||pan_y<0||pan_y>1)check(E_INVALIDARG,"invalid video viewport");
    double scale=zoom==0?std::min(static_cast<double>(width)/video_width,static_cast<double>(height)/video_height):zoom/100.0;
    struct axis {int source,count,destination,size;};
    auto calculate=[&](int client,int video,double pan)->axis{
        if(video*scale<=client){int size=std::clamp(static_cast<int>(std::round(video*scale)),1,client);return {0,video,(client-size)/2,size};}
        int count=std::clamp(static_cast<int>(std::round(client/scale)),1,video);return {static_cast<int>(std::round((video-count)*pan)),count,0,client};
    };
    auto x=calculate(width,video_width,pan_x),y=calculate(height,video_height,pan_y);
    return {x.source,y.source,x.count,y.count,x.destination,y.destination,x.size,y.size};
}
