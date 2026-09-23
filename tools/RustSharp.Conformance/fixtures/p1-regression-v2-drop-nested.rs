struct Outer;
struct Inner;

impl Drop for Outer {
    fn drop(&mut self) {
        println!("outer");
    }
}

impl Drop for Inner {
    fn drop(&mut self) {
        println!("inner");
    }
}

fn main() {
    let _outer = Outer;
    println!("nested");
    {
        let _inner = Inner;
        println!("inside");
    }
    println!("after");
}
